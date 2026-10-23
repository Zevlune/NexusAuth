using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Authorization;
using NexusAuth.Application.Services.DeviceAuthorization;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Host.Controllers;
using NexusAuth.Host.TokenEndpoint;
using NexusAuth.Shared.DependencyInjection;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public class TokenGrantStrategyTests
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    [Fact]
    public async Task Di_resolves_all_grants_and_accepts_an_additional_strategy()
    {
        var services = Services();
        services.AddScopeKeyedServices(typeof(ExtensionTokenGrantStrategy).Assembly);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var grantKeys = new[] { "authorization_code", "client_credentials", "refresh_token", DeviceGrant, WeChatMiniProgramGrantTypes.GrantType };
        foreach (var key in grantKeys)
            Assert.NotNull(scope.ServiceProvider.ResolveKeyed<ITokenGrantStrategy>(key));
        var extension = scope.ServiceProvider.ResolveKeyed<ITokenGrantStrategy>("extension");
        Assert.IsType<ExtensionTokenGrantStrategy>(extension);
        Assert.Same(extension, scope.ServiceProvider.ResolveKeyed<ITokenGrantStrategy>("extension"));
        var context = Context(new TokenRequest("extension"));
        using var cts = new CancellationTokenSource();
        var result = Assert.IsType<OkObjectResult>(await ExecuteAsync(scope.ServiceProvider, context, cts.Token));
        Assert.Equal(cts.Token, result.Value);
        AssertCacheHeaders(context);
    }

    [Theory]
    [InlineData(null, null, 400, "invalid_request")]
    [InlineData("  ", "Bearer ignored", 400, "invalid_request")]
    [InlineData("unknown", null, 400, "unsupported_grant_type")]
    [InlineData("unknown", "Bearer rejected", 401, "invalid_client")]
    [InlineData("AUTHORIZATION_CODE", null, 400, "unsupported_grant_type")]
    public async Task Missing_or_unknown_grants_preserve_error_precedence(string? grant, string? authorization, int status, string error)
    {
        using var provider = Services().BuildServiceProvider();
        var context = Context(new TokenRequest(grant));
        context.HttpContext.Request.Headers.Authorization = authorization;
        var result = await ExecuteAsync(provider, context);
        AssertError(result, status, error);
        AssertCacheHeaders(context);
        Assert.Equal(status == 401 ? "Basic realm=\"NexusAuth\"" : "", context.HttpContext.Response.Headers.WWWAuthenticate.ToString());
    }

    [Theory]
    [InlineData("authorization_code")]
    [InlineData("client_credentials")]
    [InlineData("refresh_token")]
    [InlineData(DeviceGrant)]
    public async Task Every_standard_grant_rejects_mixed_credentials_before_services_are_called(string grant)
    {
        using var provider = Services().BuildServiceProvider();
        var context = Context(new TokenRequest(grant, "client", "secret", ClientAssertionType: "jwt", ClientAssertion: "assertion"));
        AssertError(await ExecuteAsync(provider, context), 401, "invalid_client");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_credentials_strategy_passes_parsed_method_and_configured_endpoint_audience(bool basic)
    {
        ClientAuthenticationInput? captured = null;
        var services = Services();
        services.AddSingleton(Stub<IClientService>((method, args) =>
        {
            Assert.Equal(nameof(IClientService.AuthenticateClientAsync), method.Name);
            captured = Assert.IsType<ClientAuthenticationInput>(args![0]);
            Assert.Equal(true, args[1]);
            return Task.FromResult(ClientAuthenticationResult.Failure("invalid_client", "rejected"));
        }));
        using var provider = services.BuildServiceProvider();
        var context = Context(new TokenRequest("client_credentials", basic ? null : "client", basic ? null : "secret"));
        if (basic)
            context.HttpContext.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client:secret"));
        var result = await ExecuteAsync(provider, context);
        AssertError(result, 401, "invalid_client");
        Assert.NotNull(captured);
        Assert.Equal("client", captured.ClientId);
        Assert.Equal("secret", captured.ClientSecret);
        Assert.Equal(basic ? "client_secret_basic" : "client_secret_post", captured.TokenEndpointAuthMethod);
        Assert.Equal("https://issuer.example/connect/token", captured.AssertionAudience);
        Assert.Equal(
            ["https://issuer.example/connect/token", "https://issuer.example"],
            captured.GetAcceptedAssertionAudiences());
    }

    [Fact]
    public async Task Client_credentials_issuance_failure_is_reported_as_invalid_scope()
    {
        var client = OAuthClient.Create(
            "client",
            "Client",
            allowedScopes: ["orders.api"],
            allowedGrantTypes: ["client_credentials"]);
        var services = Services();
        services.AddSingleton(Stub<IClientService>((method, args) => method.Name switch
        {
            nameof(IClientService.AuthenticateClientAsync) =>
                Task.FromResult(ClientAuthenticationResult.Success(client)),
            nameof(IClientService.ValidateScopesAsync) =>
                Task.FromResult(ValidateClientCredentialsScope(args)),
            _ => null,
        }));
        services.AddSingleton(Stub<ISecurityPolicyService>((method, _) =>
            method.Name == nameof(ISecurityPolicyService.CheckClient)
                ? PolicyCheckResult.Success()
                : null));
        services.AddSingleton(Stub<ITokenService>((method, _) =>
            method.Name == nameof(ITokenService.IssueAccessTokenAsync)
                ? throw new InvalidOperationException("Requested scopes span multiple audiences.")
                : null));

        using var provider = services.BuildServiceProvider();
        var result = await ExecuteAsync(provider, Context(new TokenRequest(
            "client_credentials", "client", "secret", Scope: "orders.api")));

        var error = AssertError(result, 400, "invalid_scope");
        Assert.Equal("Requested scopes span multiple audiences.", error.GetProperty("error_description").GetString());

        static ScopeValidationResult ValidateClientCredentialsScope(object?[]? args)
        {
            Assert.Equal("client", args![0]);
            Assert.Equal("orders.api", args[1]);
            Assert.Equal(false, args[2]);
            return ScopeValidationResult.Success((string)args[1]!);
        }
    }

    [Fact]
    public async Task Device_code_poll_for_policy_blocked_user_is_rejected_before_issuance()
    {
        var user = User.Create("device-user", "test-password", "Device User");
        var policy = new TestSecurityPolicy();
        policy.BlockedUsers.Add(user.Id);
        var issued = 0;
        var services = Services();
        services.AddSingleton(Stub<IUserService>((method, args) =>
            method.Name == nameof(IUserService.FindByIdAsync) && (Guid)args![0]! == user.Id
                ? Task.FromResult<User?>(user)
                : null));
        services.AddSingleton(Stub<ISecurityPolicyService>((method, _) =>
            method.Name == nameof(ISecurityPolicyService.CheckUser) ? policy.Service.CheckUser(user) : null));
        services.AddSingleton(Stub<IDeviceAuthorizationService>((method, _) =>
            method.Name == nameof(IDeviceAuthorizationService.PollAsync)
                ? Task.FromResult(DeviceAuthorizationPollResult.Success(user.Id, "client", "openid"))
                : null));
        services.AddSingleton(Stub<ITokenService>((_, _) =>
        {
            issued++;
            return null;
        }));

        using var provider = services.BuildServiceProvider();
        var result = await ExecuteAsync(provider, Context(new TokenRequest(
            DeviceGrant, "client", DeviceCode: "device-code")));

        AssertError(result, 400, "invalid_grant");
        Assert.Equal(0, issued);
    }

    [Fact]
    public async Task Device_code_issuance_failure_returns_an_oauth_error()
    {
        var user = User.Create("device-user", "test-password", "Device User");
        var services = Services();
        services.AddSingleton(Stub<IUserService>((method, args) =>
            method.Name == nameof(IUserService.FindByIdAsync) && (Guid)args![0]! == user.Id
                ? Task.FromResult<User?>(user)
                : null));
        services.AddSingleton(Stub<ISecurityPolicyService>((method, _) =>
            method.Name == nameof(ISecurityPolicyService.CheckUser) ? PolicyCheckResult.Success() : null));
        services.AddSingleton(Stub<IDeviceAuthorizationService>((method, _) =>
            method.Name == nameof(IDeviceAuthorizationService.PollAsync)
                ? Task.FromResult(DeviceAuthorizationPollResult.Success(user.Id, "client", "openid"))
                : null));
        services.AddSingleton(Stub<ITokenService>((method, _) =>
            method.Name == nameof(ITokenService.IssueAccessTokenWithMetadataAsync)
                ? throw new InvalidOperationException("Requested scopes span multiple audiences.")
                : null));

        using var provider = services.BuildServiceProvider();
        var result = await ExecuteAsync(provider, Context(new TokenRequest(
            DeviceGrant, "client", DeviceCode: "device-code")));

        AssertError(result, 400, "invalid_grant");
    }

    [Fact]
    public async Task WeChat_issuance_failure_returns_an_oauth_error()
    {
        var user = User.Create("wechat-user", "test-password", "WeChat User");
        var client = OAuthClient.Create(
            "mini", "Mini", allowedGrantTypes: [WeChatMiniProgramGrantTypes.GrantType],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone);
        var services = Services();
        services.AddSingleton(Stub<IClientService>((method, args) => method.Name switch
        {
            nameof(IClientService.AuthenticateClientAsync) => Task.FromResult(ClientAuthenticationResult.Success(client)),
            nameof(IClientService.ValidateScopesAsync) => Task.FromResult(ScopeValidationResult.Success((string)args![1]!)),
            _ => null,
        }));
        services.AddSingleton(Stub<ISecurityPolicyService>((method, _) => method.Name switch
        {
            nameof(ISecurityPolicyService.CheckClient) => PolicyCheckResult.Success(),
            nameof(ISecurityPolicyService.CheckUser) => PolicyCheckResult.Success(),
            _ => null,
        }));
        services.AddSingleton(Stub<IWeChatMiniProgramLoginService>((method, _) =>
            method.Name == nameof(IWeChatMiniProgramLoginService.AuthenticateAsync)
                ? Task.FromResult(new WeChatMiniProgramLoginResult(
                    user, DateTimeOffset.UtcNow, "wechat", "wechat", "subject", null))
                : null));
        services.AddSingleton(Stub<ITokenService>((method, _) =>
            method.Name == nameof(ITokenService.IssueAccessTokenWithMetadataAsync)
                ? throw new InvalidOperationException("Requested scopes span multiple audiences.")
                : null));

        using var provider = services.BuildServiceProvider();
        var context = Context(new TokenRequest(
            WeChatMiniProgramGrantTypes.GrantType, "mini", Code: "code", Scope: "openid"));
        context.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        context.HttpContext.Request.ContentLength = 0;
        var result = await ExecuteAsync(provider, context);

        AssertError(result, 400, "invalid_grant");
    }

    [Theory]
    [InlineData("invalid_client", 401, "invalid_client", false)]
    [InlineData("invalid_request", 400, "invalid_request", false)]
    [InlineData("unauthorized_client", 400, "unauthorized_client", false)]
    [InlineData("access_denied", 400, "access_denied", false)]
    [InlineData("authorization_pending", 400, "authorization_pending", true)]
    [InlineData("slow_down", 400, "slow_down", true)]
    [InlineData("expired_token", 400, "expired_token", true)]
    [InlineData("unexpected", 400, "invalid_grant", false)]
    public async Task Device_poll_errors_preserve_oauth_status_and_interval(string pollError, int status, string error, bool interval)
    {
        var services = Services();
        services.AddSingleton(Stub<IDeviceAuthorizationService>((method, args) =>
        {
            Assert.Equal(nameof(IDeviceAuthorizationService.PollAsync), method.Name);
            Assert.Equal("device-code", args![1]);
            return Task.FromResult(DeviceAuthorizationPollResult.Failure(pollError, "poll failed", 9));
        }));
        using var provider = services.BuildServiceProvider();
        var context = Context(new TokenRequest(DeviceGrant, "client", DeviceCode: "device-code"));
        var result = await ExecuteAsync(provider, context);
        var json = AssertError(result, status, error);
        Assert.Equal(interval, json.TryGetProperty("interval", out var value));
        if (interval) Assert.Equal(9, value.GetInt32());
    }

    [Theory]
    [InlineData("mini", true, false)]
    [InlineData("mini", false, true)]
    [InlineData("other", true, true)]
    public async Task Refresh_authentication_policy_only_bypasses_credentials_for_enabled_configured_client(string clientId, bool enabled, bool requireAuthentication)
    {
        var services = Services();
        services.Configure<WeChatMiniProgramOptions>(options => options.Enabled = enabled);
        services.AddSingleton(Stub<IClientService>((method, args) =>
        {
            Assert.Equal(requireAuthentication, args![1]);
            return Task.FromResult(ClientAuthenticationResult.Failure("invalid_client", "rejected"));
        }));
        using var provider = services.BuildServiceProvider();
        var result = await ExecuteAsync(provider, Context(new TokenRequest("refresh_token", clientId)));
        AssertError(result, 401, "invalid_client");
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("assertion")]
    [InlineData("basic")]
    [InlineData("confidential-method")]
    public async Task Public_refresh_rejects_credentials_and_nonpublic_registration(string supplied)
    {
        var services = Services();
        services.AddSingleton(Stub<IClientService>((_, args) =>
        {
            Assert.Equal(false, args![1]);
            return Task.FromResult(ClientAuthenticationResult.Success(OAuthClient.Create(
                "mini", "Mini", allowedGrantTypes: [WeChatMiniProgramGrantTypes.GrantType], tokenEndpointAuthMethod: supplied == "confidential-method" ? "client_secret_post" : "none")));
        }));
        using var provider = services.BuildServiceProvider();
        var context = Context(new TokenRequest("refresh_token", "mini",
            ClientSecret: supplied == "secret" ? "" : null,
            ClientAssertionType: supplied == "assertion" ? "" : null));
        if (supplied == "basic")
            context.HttpContext.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("mini:secret"));
        AssertError(await ExecuteAsync(provider, context), 401, "invalid_client");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_rotates_tokens_using_the_matching_client_policy(bool publicClient)
    {
        var clientId = publicClient ? "mini" : "client";
        var client = OAuthClient.Create(clientId, "Refresh client",
            allowedGrantTypes: ["refresh_token", WeChatMiniProgramGrantTypes.GrantType],
            tokenEndpointAuthMethod: publicClient ? "none" : "client_secret_post");
        var services = Services();
        services.AddSingleton(Stub<IClientService>((_, args) =>
        {
            Assert.Equal(!publicClient, args![1]);
            return Task.FromResult(ClientAuthenticationResult.Success(client));
        }));
        services.AddSingleton(Stub<ISecurityPolicyService>((_, _) => PolicyCheckResult.Success()));
        services.AddSingleton(Stub<ITokenService>((method, args) =>
        {
            Assert.Equal(nameof(ITokenService.RefreshAsync), method.Name);
            Assert.Equal("original-refresh", args![0]);
            Assert.Equal(clientId, args[1]);
            return Task.FromResult(RefreshResult.Success("new-access", "new-refresh"));
        }));
        using var provider = services.BuildServiceProvider();
        var result = await ExecuteAsync(provider, Context(
            new TokenRequest("refresh_token", clientId, RefreshToken: "original-refresh")));
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("new-access", json.GetProperty("access_token").GetString());
        Assert.Equal("new-refresh", json.GetProperty("refresh_token").GetString());
        Assert.False(json.TryGetProperty("id_token", out _));
    }

    [Theory]
    [InlineData("openid profile", false, true)]
    [InlineData("openid offline_access", true, true)]
    [InlineData("api.read", false, false)]
    public async Task Authorization_code_preserves_oidc_context_and_scope_dependent_tokens(string requestedScope, bool refresh, bool idToken)
    {
        var user = User.Create("strategy-user", "test-password", "昵称");
        var authenticatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var calls = new List<string>();
        var userPolicyChecks = 0;
        var services = Services();
        services.AddSingleton(Stub<IClientService>((_, _) => Task.FromResult(ClientAuthenticationResult.Success(
            OAuthClient.Create("client", "Test")))));
        services.AddSingleton(Stub<IAuthorizationService>((method, args) =>
        {
            Assert.Equal(nameof(IAuthorizationService.ValidateAndConsumeCodeAsync), method.Name);
            Assert.Equal(new object?[] { "code", "client", "https://client/callback", "verifier" }, args![..4]);
            return Task.FromResult(AuthorizationCodeResult.Success(user.Id, "client", requestedScope, "nonce", "{}", authenticatedAt, "acr", "pwd"));
        }));
        services.AddSingleton(Stub<IUserService>((_, _) => Task.FromResult<User?>(user)));
        services.AddSingleton(Stub<ISecurityPolicyService>((method, _) =>
        {
            Assert.Equal(nameof(ISecurityPolicyService.CheckUser), method.Name);
            userPolicyChecks++;
            return PolicyCheckResult.Success();
        }));
        services.AddSingleton(Stub<ITokenService>((method, args) =>
        {
            calls.Add(method.Name);
            switch (method.Name)
            {
                case nameof(ITokenService.IssueAccessTokenWithMetadataAsync):
                    Assert.Equal("{}", args![4]);
                    return Task.FromResult(new TokenIssueResult("access", "jti", DateTime.UtcNow.AddMinutes(5)));
                case nameof(ITokenService.IssueRefreshTokenAsync): return Task.FromResult("refresh");
                case nameof(ITokenService.IssueIdTokenAsync):
                    Assert.Equal(new object?[] { "client", user.Id, "nonce", "access", "{}", authenticatedAt, "acr", "pwd" }, args![..8]);
                    return Task.FromResult("id");
                default: throw new InvalidOperationException(method.Name);
            }
        }));
        using var provider = services.BuildServiceProvider();
        var result = await ExecuteAsync(provider, Context(new TokenRequest(
            "authorization_code", "client", Code: "code", RedirectUri: "https://client/callback", CodeVerifier: "verifier")));
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("access", json.GetProperty("access_token").GetString());
        Assert.Equal(refresh ? "refresh" : null, json.GetProperty("refresh_token").GetString());
        Assert.Equal(idToken ? "id" : null, json.GetProperty("id_token").GetString());
        Assert.Equal(300, json.GetProperty("expires_in").GetInt32());
        Assert.Equal(refresh, calls.Contains(nameof(ITokenService.IssueRefreshTokenAsync)));
        Assert.Equal(idToken, calls.Contains(nameof(ITokenService.IssueIdTokenAsync)));
        Assert.Equal(1, userPolicyChecks);
    }

    private static IServiceCollection Services()
    {
        var services = AutoDependencyServices.Create();
        services.AddOptions();
        services.Configure<JwtOptions>(options => { options.Issuer = "https://issuer.example"; options.AccessTokenLifetimeMinutes = 5; });
        services.Configure<WeChatMiniProgramOptions>(options => { options.ClientId = "mini"; options.Enabled = true; });
        services.AddSingleton(Stub<IClientService>());
        services.AddSingleton(Stub<ITokenService>());
        services.AddSingleton(Stub<IAuthorizationService>());
        services.AddSingleton(Stub<IDeviceAuthorizationService>());
        services.AddSingleton(Stub<IUserService>());
        services.AddSingleton(Stub<ISecurityPolicyService>());
        services.AddSingleton(Stub<IWeChatMiniProgramLoginService>());
        services.AddSingleton(Stub<IUserRepository>());
        return services.AddTokenGrantStrategies();
    }

    private static TokenGrantContext Context(TokenRequest input)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example");
        context.Request.PathBase = "/identity";
        context.Request.Path = "/connect/token";
        return new TokenGrantContext(context, input);
    }

    private static Task<IActionResult> ExecuteAsync(
        IServiceProvider serviceProvider,
        TokenGrantContext context,
        CancellationToken ct = default)
    {
        var controller = new TokenController(serviceProvider)
        {
            ControllerContext = new ControllerContext { HttpContext = context.HttpContext }
        };
        var request = context.Request;
        return controller.Token(
            request.GrantType,
            request.ClientId,
            request.ClientSecret,
            request.Code,
            request.RedirectUri,
            request.CodeVerifier,
            request.Scope,
            request.RefreshToken,
            request.DeviceCode,
            request.ClientAssertionType,
            request.ClientAssertion,
            request.Nickname,
            ct);
    }

    private static JsonElement AssertError(IActionResult result, int status, string error)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, value.StatusCode);
        var json = JsonSerializer.SerializeToElement(value.Value);
        Assert.Equal(error, json.GetProperty("error").GetString());
        return json;
    }

    private static void AssertCacheHeaders(TokenGrantContext context)
    {
        Assert.Equal("no-store", context.HttpContext.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", context.HttpContext.Response.Headers.Pragma.ToString());
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var service = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)service).Handler = handler ?? ((method, _) => throw new InvalidOperationException($"Unexpected call: {method.Name}"));
        return service;
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

}
