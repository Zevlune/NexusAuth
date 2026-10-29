using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Authorization;
using NexusAuth.Application.Services.DeviceAuthorization;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Host.Controllers;
using NexusAuth.Host.TokenEndpoint;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class TokenEndpointHardeningTests : IClassFixture<TokenEndpointHardeningTests.SharedUser>
{
    private const string Issuer = InMemoryTokenServiceHarness.Issuer;
    private const string RedirectUri = "https://client.example/callback";
    private readonly User user;

    public TokenEndpointHardeningTests(SharedUser shared)
    {
        user = shared.User;
    }

    [Fact]
    public async Task Code_verifier_for_a_code_issued_without_pkce_is_rejected()
    {
        var code = AuthorizationCode.Create("web-client", user.Id, RedirectUri, OidcScopes.OpenId);
        var consumed = 0;
        var codes = HardeningProxy.Create<IAuthorizationCodeRepository>((method, _) =>
        {
            switch (method.Name)
            {
                case nameof(IAuthorizationCodeRepository.FindByCodeAsync):
                    return code.Entity;
                case nameof(IAuthorizationCodeRepository.ConsumeAsync):
                    consumed++;
                    return code.Entity;
                default:
                    return null;
            }
        });
        var authorizationValidations = 0;
        var clients = HardeningProxy.Create<IClientService>((method, args) => method.Name switch
        {
            nameof(IClientService.ValidateClientForAuthorizationAsync) => IncrementAndAcceptAuthorization(),
            nameof(IClientService.ValidateScopesAsync) => ScopeValidationResult.Success((string)args![1]!),
            _ => null,
        });
        var service = new AuthorizationService(codes, clients, new TestSecurityPolicy().Service, NullLogger<AuthorizationService>.Instance);

        var downgrade = await service.ValidateAndConsumeCodeAsync(code.RawCode, "web-client", RedirectUri, "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.False(downgrade.IsSuccess);
        Assert.Contains("did not use PKCE", downgrade.Error);
        Assert.Equal(0, consumed);
        Assert.Equal(0, authorizationValidations);

        var plain = await service.ValidateAndConsumeCodeAsync(code.RawCode, "web-client", RedirectUri);
        Assert.True(plain.IsSuccess);
        Assert.Equal(1, consumed);
        Assert.Equal(1, authorizationValidations);

        ClientValidationResult IncrementAndAcceptAuthorization()
        {
            authorizationValidations++;
            return ClientValidationResult.Success();
        }
    }

    [Fact]
    public async Task Token_endpoint_derives_assertion_audiences_from_the_configured_issuer_not_the_host_header()
    {
        var inputs = new List<ClientAuthenticationInput>();
        using var services = TokenControllerServices(
            HardeningClientServices.AcceptingAuthorizeRequests(inputs),
            tokenService: null);
        var controller = CreateTokenController(services);
        controller.HttpContext.Request.Scheme = "http";
        controller.HttpContext.Request.Host = new HostString("evil.example");
        controller.HttpContext.Request.Path = "/connect/token";

        var result = await controller.Token(
            OAuthGrantTypes.ClientCredentials,
            clientId: "pk-client",
            scope: "orders.api",
            clientAssertionType: OAuthClientAssertionTypes.JwtBearer,
            clientAssertion: "header.payload.signature");

        Assert.IsType<UnauthorizedObjectResult>(result);
        var audiences = Assert.Single(inputs).GetAcceptedAssertionAudiences();
        Assert.Equal([$"{Issuer}/connect/token", Issuer], audiences);
    }

    [Fact]
    public async Task Introspection_accepts_issuer_token_endpoint_and_endpoint_url_audiences_only()
    {
        var inputs = new List<ClientAuthenticationInput>();
        var controller = CreateOpenIdController(HardeningClientServices.AcceptingAuthorizeRequests(inputs), tokenService: null!);
        controller.HttpContext.Request.Host = new HostString("evil.example");

        await controller.Introspect(
            "pk-client",
            null,
            OAuthClientAssertionTypes.JwtBearer,
            "header.payload.signature",
            "token");

        var audiences = Assert.Single(inputs).GetAcceptedAssertionAudiences();
        Assert.Equal([$"{Issuer}/connect/token", Issuer, $"{Issuer}/connect/introspect"], audiences);
    }

    [Fact]
    public async Task Public_client_can_revoke_its_own_refresh_token_with_client_id_only()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var publicClient = OAuthClient.Create(
            "miniapp",
            "Mini program",
            allowedGrantTypes: [OAuthGrantTypes.WeChatMiniProgram, OAuthGrantTypes.RefreshToken],
            requirePkce: false,
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None);
        var refreshToken = await harness.Service.IssueRefreshTokenAsync(
            "miniapp",
            user.Id,
            $"{OidcScopes.OpenId} {OidcScopes.OfflineAccess}");
        var controller = CreateOpenIdController(ClientLookup(publicClient), harness.Service);

        var result = await controller.Revoke(
            "miniapp",
            null,
            null,
            null,
            refreshToken,
            OAuthGrantTypes.RefreshToken);

        Assert.IsType<OkResult>(result);
        Assert.True(harness.IsRefreshTokenRevoked(refreshToken));
    }

    [Fact]
    public async Task Public_client_revocation_with_credentials_or_confidential_client_without_credentials_is_rejected()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var publicClient = OAuthClient.Create(
            "miniapp",
            "Mini program",
            allowedGrantTypes: [OAuthGrantTypes.WeChatMiniProgram, OAuthGrantTypes.RefreshToken],
            requirePkce: false,
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.None);
        var confidentialClient = OAuthClient.Create(
            "web-client",
            "Web",
            allowedGrantTypes: [OAuthGrantTypes.AuthorizationCode, OAuthGrantTypes.RefreshToken]);
        var confidentialToken = await harness.Service.IssueRefreshTokenAsync(
            "web-client",
            user.Id,
            $"{OidcScopes.OpenId} {OidcScopes.OfflineAccess}");

        var withSecret = await CreateOpenIdController(ClientLookup(publicClient), harness.Service)
            .Revoke("miniapp", "unexpected-secret", null, null, "token", null);
        var unauthenticatedConfidential = await CreateOpenIdController(ClientLookup(confidentialClient), harness.Service)
            .Revoke("web-client", null, null, null, confidentialToken, OAuthGrantTypes.RefreshToken);

        Assert.IsType<UnauthorizedObjectResult>(withSecret);
        Assert.IsType<UnauthorizedObjectResult>(unauthenticatedConfidential);
        Assert.False(harness.IsRefreshTokenRevoked(confidentialToken));
    }

    [Fact]
    public async Task Revocation_with_a_wrong_token_type_hint_still_revokes_the_access_token()
    {
        using var harness = new InMemoryTokenServiceHarness();
        harness.Add(user);
        var confidentialClient = OAuthClient.Create(
            "web-client",
            "Web",
            allowedGrantTypes: [OAuthGrantTypes.AuthorizationCode]);
        var accessToken = await harness.Service.IssueAccessTokenAsync("web-client", OidcScopes.OpenId, null, user.Id);
        Assert.True((await harness.Service.IntrospectAsync(accessToken, "web-client")).Active);
        var controller = CreateOpenIdController(ClientLookup(confidentialClient, authenticates: true), harness.Service);

        var result = await controller.Revoke(
            "web-client",
            "secret",
            null,
            null,
            accessToken,
            OAuthGrantTypes.RefreshToken);

        Assert.IsType<OkResult>(result);
        Assert.False((await harness.Service.IntrospectAsync(accessToken, "web-client")).Active);
    }

    [Fact]
    public async Task Token_issuance_failure_after_code_consumption_returns_an_oauth_error()
    {
        var tokenService = HardeningProxy.Create<ITokenService>((method, _) =>
            method.Name == nameof(ITokenService.IssueAccessTokenWithMetadataAsync)
                ? throw new InvalidOperationException("Requested scopes span multiple audiences.")
                : null);
        using var services = TokenControllerServices(ConfidentialClient(), tokenService, CodeExchange());
        var controller = CreateTokenController(services);

        var result = await controller.Token(
            OAuthGrantTypes.AuthorizationCode,
            "web-client",
            "secret",
            "code",
            RedirectUri);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("invalid_grant", JsonSerializer.SerializeToElement(badRequest.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Code_exchange_for_a_policy_blocked_user_is_rejected()
    {
        var policy = new TestSecurityPolicy();
        policy.BlockedUsers.Add(user.Id);
        var issued = 0;
        var tokenService = HardeningProxy.Create<ITokenService>((method, _) =>
        {
            issued++;
            return null;
        });
        using var services = TokenControllerServices(ConfidentialClient(), tokenService, CodeExchange(), policy);
        var controller = CreateTokenController(services);

        var result = await controller.Token(
            OAuthGrantTypes.AuthorizationCode,
            "web-client",
            "secret",
            "code",
            RedirectUri);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("invalid_grant", JsonSerializer.SerializeToElement(badRequest.Value).GetProperty("error").GetString());
        Assert.Equal(0, issued);
    }

    private IAuthorizationService CodeExchange() => HardeningProxy.Create<IAuthorizationService>((method, _) =>
        method.Name == nameof(IAuthorizationService.ValidateAndConsumeCodeAsync)
            ? AuthorizationCodeResult.Success(user.Id, "web-client", OidcScopes.OpenId, null, null, DateTimeOffset.UtcNow, null, null)
            : null);

    private static IClientService ConfidentialClient()
    {
        var client = OAuthClient.Create(
            "web-client",
            "Web",
            allowedGrantTypes: [OAuthGrantTypes.AuthorizationCode]);
        return HardeningProxy.Create<IClientService>((method, _) =>
            method.Name == nameof(IClientService.AuthenticateClientAsync) ? ClientAuthenticationResult.Success(client) : null);
    }

    private static IClientService ClientLookup(OAuthClient client, bool authenticates = false) =>
        HardeningProxy.Create<IClientService>((method, args) =>
        {
            if (method.Name != nameof(IClientService.AuthenticateClientAsync) || args![0] is not ClientAuthenticationInput)
                return null;

            var requireAuthentication = (bool)args[1]!;
            return !requireAuthentication || authenticates
                ? ClientAuthenticationResult.Success(client)
                : ClientAuthenticationResult.Failure("invalid_client", "client_secret is required.");
        });

    private ServiceProvider TokenControllerServices(
        IClientService clientService,
        ITokenService? tokenService,
        IAuthorizationService? authorizationService = null,
        TestSecurityPolicy? policy = null)
    {
        var users = HardeningProxy.Create<IUserService>((method, args) =>
            method.Name == nameof(IUserService.FindByIdAsync) && (Guid)args![0]! == user.Id ? user : null);
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<JwtOptions>(options => options.Issuer = Issuer + "/");
        services.AddSingleton(clientService);
        services.AddSingleton(tokenService ?? HardeningProxy.Create<ITokenService>((_, _) => null));
        services.AddSingleton(authorizationService ?? HardeningProxy.Create<IAuthorizationService>((_, _) => null));
        services.AddSingleton(users);
        services.AddSingleton((policy ?? new TestSecurityPolicy()).Service);
        services.AddSingleton(HardeningProxy.Create<IDeviceAuthorizationService>((_, _) => null));
        services.AddSingleton(HardeningProxy.Create<IWeChatMiniProgramLoginService>((_, _) => null));
        services.AddSingleton(HardeningProxy.Create<IUserRepository>((_, _) => null));
        services.AddTokenGrantStrategies();
        return services.BuildServiceProvider();
    }

    private static TokenController CreateTokenController(IServiceProvider services)
    {
        var controller = new TokenController(services);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static OpenIdController CreateOpenIdController(IClientService clientService, ITokenService tokenService)
    {
        var controller = new OpenIdController(
            signingCredentialsProvider: null!,
            idTokenHintValidator: null!,
            tokenService: tokenService,
            userService: null!,
            clientService: clientService,
            deviceAuthorizationService: null!,
            sessionService: null!,
            antiforgery: null!,
            jwtOptions: Options.Create(new JwtOptions { Issuer = Issuer }));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    public sealed class SharedUser
    {
        public User User { get; } = User.Create($"token-{Guid.NewGuid():N}", "Test-password-123!", "Token User");
    }
}
