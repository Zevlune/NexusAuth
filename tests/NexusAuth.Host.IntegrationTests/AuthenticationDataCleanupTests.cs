using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.DataRetention;
using NexusAuth.Application.Services.DeviceAuthorization;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class AuthenticationDataCleanupTests
{
    [Fact]
    public async Task Cleanup_service_applies_configured_retention_and_batch_limits()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var expected = new AuthenticationDataCleanupResult(1, 2, 3, 4, 5, 6, 7);
        DateTimeOffset? expiredAtOrBefore = null;
        DateTimeOffset? loginAuditOccurredAtOrBefore = null;
        int? batchSize = null;
        int? maxBatches = null;
        var repository = HardeningProxy.Create<IAuthenticationDataCleanupRepository>((method, args) =>
        {
            if (method.Name != nameof(IAuthenticationDataCleanupRepository.CleanupAsync))
                return null;

            Assert.Equal(now, (DateTimeOffset)args![0]!);
            expiredAtOrBefore = (DateTimeOffset)args[1]!;
            loginAuditOccurredAtOrBefore = (DateTimeOffset)args[2]!;
            batchSize = (int)args[3]!;
            maxBatches = (int)args[4]!;
            return expected;
        });
        var service = new AuthenticationDataCleanupService(
            repository,
            Options.Create(new AuthenticationDataRetentionOptions
            {
                ExpiredDataRetentionDays = 7,
                LoginAuditRetentionDays = 180,
                CleanupBatchSize = 250,
                MaxBatchesPerTypePerSweep = 4,
            }),
            new FixedTimeProvider(now));

        var result = await service.CleanupAsync();

        Assert.Same(expected, result);
        Assert.Equal(now.AddDays(-7), expiredAtOrBefore);
        Assert.Equal(now.AddDays(-180), loginAuditOccurredAtOrBefore);
        Assert.Equal(250, batchSize);
        Assert.Equal(4, maxBatches);
    }

    [Fact]
    public async Task Device_authorization_start_regenerates_a_user_code_after_a_unique_conflict()
    {
        var attempts = 0;
        var service = CreateDeviceAuthorizationService(() =>
        {
            attempts++;
            return attempts >= 3;
        });

        var result = await service.StartAsync(DeviceAuthentication(), "openid");

        Assert.True(result.IsSuccess);
        Assert.Equal(3, attempts);
        Assert.False(string.IsNullOrWhiteSpace(result.UserCode));
    }

    [Fact]
    public async Task Device_authorization_start_returns_a_protocol_error_after_exhausting_retries()
    {
        var attempts = 0;
        var service = CreateDeviceAuthorizationService(() =>
        {
            attempts++;
            return false;
        });

        var result = await service.StartAsync(DeviceAuthentication(), "openid");

        Assert.False(result.IsSuccess);
        Assert.Equal("temporarily_unavailable", result.ErrorCode);
        Assert.Equal(5, attempts);
    }

    private static DeviceAuthorizationService CreateDeviceAuthorizationService(Func<bool> tryAdd)
    {
        var client = OAuthClient.Create(
            "device-client",
            "Device client",
            allowedScopes: ["openid"],
            allowedGrantTypes: ["urn:ietf:params:oauth:grant-type:device_code"],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodClientSecretPost,
            clientSecrets:
            [
                NexusAuth.Domain.Entities.OAuthClientSecret.CreateSharedSecret(Guid.NewGuid(), "device-secret"),
            ]);
        var clients = HardeningProxy.Create<IClientService>((method, args) => method.Name switch
        {
            nameof(IClientService.AuthenticateClientAsync) => ClientAuthenticationResult.Success(client),
            nameof(IClientService.ValidateScopesAsync) => ScopeValidationResult.Success((string)args![1]!),
            _ => null,
        });
        var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, _) =>
            method.Name == nameof(IDeviceAuthorizationRepository.TryAddAsync) ? tryAdd() : null);

        return new DeviceAuthorizationService(
            clients,
            devices,
            HardeningProxy.Create<IUserRepository>((_, _) => null),
            new TestSecurityPolicy().Service,
            Options.Create(new JwtOptions
            {
                DeviceCodeLifetimeMinutes = 15,
            }));
    }

    private static ClientAuthenticationInput DeviceAuthentication() => new(
        "device-client",
        ClientSecret: null,
        ClientAssertionType: null,
        ClientAssertion: null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
