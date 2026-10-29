using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.Authorization;
using NexusAuth.Application.Services.DeviceAuthorization;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class AuthorizationGrantRevalidationTests
{
    private const string ClientId = "web-client";
    private const string RedirectUri = "https://client.example/callback";

    [Fact]
    public async Task Authorization_code_exchange_revalidates_the_current_grant_before_consumption()
    {
        var code = CreateCode();
        var consumed = 0;
        var scopeChecked = false;
        var clientService = ExchangeClientService(
            validateClient: () => ClientValidationResult.Failure("unauthorized_client", "The grant was revoked."),
            validateScope: () =>
            {
                scopeChecked = true;
                return ScopeValidationResult.Success(code.Entity.Scope);
            });
        var service = CreateAuthorizationService(code, clientService, () => consumed++);

        var result = await service.ValidateAndConsumeCodeAsync(code.RawCode, ClientId, RedirectUri);

        Assert.False(result.IsSuccess);
        Assert.Equal("The grant was revoked.", result.Error);
        Assert.False(scopeChecked);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task Authorization_code_exchange_revalidates_the_current_redirect_before_consumption()
    {
        var code = CreateCode();
        var consumed = 0;
        var scopeChecked = false;
        var clientService = ExchangeClientService(
            validateClient: () => ClientValidationResult.Failure("invalid_request", "The redirect URI was revoked."),
            validateScope: () =>
            {
                scopeChecked = true;
                return ScopeValidationResult.Success(code.Entity.Scope);
            });
        var service = CreateAuthorizationService(code, clientService, () => consumed++);

        var result = await service.ValidateAndConsumeCodeAsync(code.RawCode, ClientId, RedirectUri);

        Assert.False(result.IsSuccess);
        Assert.Equal("The redirect URI was revoked.", result.Error);
        Assert.False(scopeChecked);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task Authorization_code_exchange_revalidates_the_current_scope_before_consumption()
    {
        var code = CreateCode();
        var consumed = 0;
        var clientService = ExchangeClientService(
            validateClient: () => ClientValidationResult.Success(),
            validateScope: () => ScopeValidationResult.Failure("invalid_scope", "The scope was removed."));
        var service = CreateAuthorizationService(code, clientService, () => consumed++);

        var result = await service.ValidateAndConsumeCodeAsync(code.RawCode, ClientId, RedirectUri);

        Assert.False(result.IsSuccess);
        Assert.Equal("The scope was removed.", result.Error);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task Authorization_code_exchange_rejects_an_inactive_resource_without_consuming_the_code()
    {
        var code = CreateCode();
        var consumed = 0;
        var clientService = ExchangeClientService(
            validateClient: () => ClientValidationResult.Success(),
            validateScope: () => ScopeValidationResult.Failure("invalid_scope", "The resource is inactive."));
        var service = CreateAuthorizationService(code, clientService, () => consumed++);

        var result = await service.ValidateAndConsumeCodeAsync(code.RawCode, ClientId, RedirectUri);

        Assert.False(result.IsSuccess);
        Assert.Equal("The resource is inactive.", result.Error);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task Authorization_code_exchange_consumes_only_after_current_client_and_scope_validation_succeed()
    {
        var code = CreateCode();
        var consumed = 0;
        var clientChecked = false;
        var scopeChecked = false;
        var clientService = HardeningProxy.Create<IClientService>((method, args) => method.Name switch
        {
            nameof(IClientService.ValidateClientForAuthorizationAsync) => ValidateAuthorization(args),
            nameof(IClientService.ValidateScopesAsync) => ValidateScope(args),
            _ => null,
        });
        var service = CreateAuthorizationService(code, clientService, () => consumed++);

        var result = await service.ValidateAndConsumeCodeAsync(code.RawCode, ClientId, RedirectUri);

        Assert.True(result.IsSuccess);
        Assert.Equal(code.Entity.Scope, result.Scope);
        Assert.Equal(1, consumed);
        Assert.True(clientChecked);
        Assert.True(scopeChecked);

        ClientValidationResult ValidateAuthorization(object?[]? args)
        {
            clientChecked = true;
            Assert.Equal(ClientId, args![0]);
            Assert.Equal(RedirectUri, args[1]);
            Assert.Equal("authorization_code", args[2]);
            Assert.Equal(code.Entity.CodeChallenge, args[3]);
            Assert.Equal(code.Entity.CodeChallengeMethod, args[4]);
            return ClientValidationResult.Success();
        }

        ScopeValidationResult ValidateScope(object?[]? args)
        {
            scopeChecked = true;
            Assert.Equal(ClientId, args![0]);
            Assert.Equal(code.Entity.Scope, args[1]);
            Assert.Equal(true, args[2]);
            return ScopeValidationResult.Success((string)args[1]!);
        }
    }

    [Fact]
    public async Task Approved_device_poll_rejects_a_removed_scope_without_consuming_the_authorization()
    {
        var user = CreateUser();
        var authorization = DeviceAuthorization.Create(ClientId, OidcScopes.OpenId).Entity;
        authorization.Approve(user.Id);
        var consumed = 0;
        var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, _) => method.Name switch
        {
            nameof(IDeviceAuthorizationRepository.FindByDeviceCodeHashAsync) => authorization,
            nameof(IDeviceAuthorizationRepository.ConsumeApprovedAsync) =>
                IncrementAndReturnApproved(ref consumed, authorization),
            _ => null,
        });
        var service = CreateDeviceAuthorizationService(
            user,
            DeviceClientService(() => ScopeValidationResult.Failure("invalid_scope", "The scope was removed.")),
            devices);

        var result = await service.PollAsync(DeviceAuthentication(), "device-code");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task Pending_device_poll_revalidates_scope_before_a_concurrent_approval_can_be_consumed()
    {
        var user = CreateUser();
        var pending = DeviceAuthorization.Create(ClientId, OidcScopes.OpenId).Entity;
        var approved = DeviceAuthorization.Create(ClientId, OidcScopes.OpenId).Entity;
        approved.Approve(user.Id);
        var finds = 0;
        var pollRegistrations = 0;
        var consumed = 0;
        var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, _) => method.Name switch
        {
            nameof(IDeviceAuthorizationRepository.FindByDeviceCodeHashAsync) =>
                finds++ == 0 ? pending : approved,
            nameof(IDeviceAuthorizationRepository.TryRegisterPollAsync) =>
                IncrementAndReturn(ref pollRegistrations, false),
            nameof(IDeviceAuthorizationRepository.ConsumeApprovedAsync) =>
                IncrementAndReturnApproved(ref consumed, approved),
            _ => null,
        });
        var service = CreateDeviceAuthorizationService(
            user,
            DeviceClientService(() => ScopeValidationResult.Failure("invalid_scope", "The scope was removed.")),
            devices);

        var result = await service.PollAsync(DeviceAuthentication(), "device-code");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
        Assert.Equal(0, pollRegistrations);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task Device_approval_rechecks_the_current_scope_before_approving()
    {
        var user = CreateUser();
        var authorization = DeviceAuthorization.Create(ClientId, OidcScopes.OpenId).Entity;
        var approvals = 0;
        var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, _) => method.Name switch
        {
            nameof(IDeviceAuthorizationRepository.FindByUserCodeAsync) => authorization,
            nameof(IDeviceAuthorizationRepository.ApprovePendingAsync) =>
                IncrementAndReturn(ref approvals, true),
            _ => null,
        });
        var service = CreateDeviceAuthorizationService(
            user,
            DeviceClientService(() => ScopeValidationResult.Failure("invalid_scope", "The scope was removed.")),
            devices);

        var result = await service.ApproveAsync(authorization.UserCode, user.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal("The scope was removed.", result.Error);
        Assert.Equal(0, approvals);
    }

    [Fact]
    public async Task Approved_device_poll_succeeds_after_current_scope_validation()
    {
        var user = CreateUser();
        var authorization = DeviceAuthorization.Create(ClientId, OidcScopes.OpenId).Entity;
        authorization.Approve(user.Id);
        var consumed = 0;
        var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, _) => method.Name switch
        {
            nameof(IDeviceAuthorizationRepository.FindByDeviceCodeHashAsync) => authorization,
            nameof(IDeviceAuthorizationRepository.ConsumeApprovedAsync) =>
                IncrementAndReturnApproved(ref consumed, authorization),
            _ => null,
        });
        var service = CreateDeviceAuthorizationService(
            user,
            DeviceClientService(() => ScopeValidationResult.Success(OidcScopes.OpenId)),
            devices);

        var result = await service.PollAsync(DeviceAuthentication(), "device-code");

        Assert.True(result.IsSuccess);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(OidcScopes.OpenId, result.Scope);
        Assert.Equal(1, consumed);
    }

    private static AuthorizationCodeCreationResult CreateCode() =>
        AuthorizationCode.Create(ClientId, CreateUser().Id, RedirectUri, OidcScopes.OpenId);

    private static User CreateUser() =>
        User.Create($"grant-revalidation-{Guid.NewGuid():N}", "Test-password-123!", "Grant Revalidation User");

    private static AuthorizationService CreateAuthorizationService(
        AuthorizationCodeCreationResult code,
        IClientService clientService,
        Action onConsume)
    {
        var codes = HardeningProxy.Create<IAuthorizationCodeRepository>((method, _) => method.Name switch
        {
            nameof(IAuthorizationCodeRepository.FindByCodeAsync) => code.Entity,
            nameof(IAuthorizationCodeRepository.ConsumeAsync) =>
                ConsumeAndReturn(onConsume, code.Entity),
            _ => null,
        });
        return new AuthorizationService(
            codes,
            clientService,
            new TestSecurityPolicy().Service,
            NullLogger<AuthorizationService>.Instance);
    }

    private static IClientService ExchangeClientService(
        Func<ClientValidationResult> validateClient,
        Func<ScopeValidationResult> validateScope) =>
        HardeningProxy.Create<IClientService>((method, _) => method.Name switch
        {
            nameof(IClientService.ValidateClientForAuthorizationAsync) => validateClient(),
            nameof(IClientService.ValidateScopesAsync) => validateScope(),
            _ => null,
        });

    private static IClientService DeviceClientService(Func<ScopeValidationResult> validateScope)
    {
        var client = OAuthClient.Create(
            ClientId,
            "Web client",
            allowedScopes: OidcScopes.All,
            allowedGrantTypes: [OAuthGrantTypes.DeviceCode],
            tokenEndpointAuthMethod: OAuthClientAuthenticationMethods.ClientSecretPost,
            clientSecrets:
            [
                OAuthClientSecret.CreateSharedSecret(Guid.NewGuid(), "device-client-secret"),
            ]);
        return HardeningProxy.Create<IClientService>((method, _) => method.Name switch
        {
            nameof(IClientService.AuthenticateClientAsync) => ClientAuthenticationResult.Success(client),
            nameof(IClientService.ValidateScopesAsync) => validateScope(),
            _ => null,
        });
    }

    private static DeviceAuthorizationService CreateDeviceAuthorizationService(
        User user,
        IClientService clientService,
        IDeviceAuthorizationRepository devices)
    {
        var users = HardeningProxy.Create<IUserRepository>((method, args) =>
            method.Name == nameof(IUserRepository.FindByIdAsync) && (Guid)args![0]! == user.Id
                ? user
                : null);
        return new DeviceAuthorizationService(
            clientService,
            devices,
            users,
            new TestSecurityPolicy().Service,
            Options.Create(new JwtOptions()));
    }

    private static ClientAuthenticationInput DeviceAuthentication() => new(
        ClientId,
        ClientSecret: null,
        ClientAssertionType: null,
        ClientAssertion: null);

    private static AuthorizationCode ConsumeAndReturn(Action onConsume, AuthorizationCode code)
    {
        onConsume();
        return code;
    }

    private static DeviceAuthorization? IncrementAndReturnApproved(
        ref int consumed,
        DeviceAuthorization authorization)
    {
        consumed++;
        return authorization;
    }

    private static bool IncrementAndReturn(ref int count, bool result)
    {
        count++;
        return result;
    }
}
