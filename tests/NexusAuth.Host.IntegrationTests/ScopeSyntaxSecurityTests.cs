using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusAuth.Application.Clients;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ScopeSyntaxSecurityTests
{
    [Fact]
    public async Task Scope_validation_accepts_and_normalizes_existing_ascii_space_behavior()
    {
        using var harness = new ClientServiceHarness();
        var input = $"  {OidcScopes.OpenId}   {OidcScopes.Profile} {OidcScopes.OpenId}  ";

        var result = await harness.Service.ValidateScopesAsync(
            ClientServiceHarness.ClientId,
            input,
            allowIdentityScopes: true);

        Assert.True(result.IsSuccess);
        Assert.Equal($"{OidcScopes.OpenId} {OidcScopes.Profile}", result.NormalizedScope);
        Assert.Equal(1, harness.ClientLookups);
    }

    [Theory]
    [InlineData(OidcScopes.OpenId + "\t" + OidcScopes.Profile)]
    [InlineData(OidcScopes.OpenId + "\r\n" + OidcScopes.Profile)]
    [InlineData(OidcScopes.OpenId + "\"" + OidcScopes.Profile)]
    [InlineData(OidcScopes.OpenId + "\\" + OidcScopes.Profile)]
    [InlineData(OidcScopes.OpenId + "\u00E9")]
    public async Task Scope_validation_rejects_non_oauth_separators_and_characters(string scope)
    {
        using var harness = new ClientServiceHarness();

        var result = await harness.Service.ValidateScopesAsync(
            ClientServiceHarness.ClientId,
            scope,
            allowIdentityScopes: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_scope", result.ErrorCode);
        Assert.Equal("scope contains invalid characters.", result.Error);
        Assert.Equal(0, harness.ClientLookups);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Scope_validation_preserves_required_error_for_missing_scope(string? scope)
    {
        using var harness = new ClientServiceHarness();

        var result = await harness.Service.ValidateScopesAsync(
            ClientServiceHarness.ClientId,
            scope!,
            allowIdentityScopes: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_scope", result.ErrorCode);
        Assert.Equal("scope is required.", result.Error);
        Assert.Equal(0, harness.ClientLookups);
    }

    private sealed class ClientServiceHarness : IDisposable
    {
        public const string ClientId = "scope-syntax-client";

        private readonly ServiceProvider serviceProvider;

        public ClientServiceHarness()
        {
            var client = OAuthClient.Create(
                ClientId,
                "Scope syntax client",
                allowedScopes: OidcScopes.All);
            var clients = HardeningProxy.Create<IOAuthClientRepository>((method, args) =>
            {
                if (method.Name != nameof(IOAuthClientRepository.FindByClientIdAsync)
                    || (string)args![0]! != ClientId)
                {
                    return null;
                }

                ClientLookups++;
                return client;
            });
            serviceProvider = new ServiceCollection().BuildServiceProvider();
            Service = new ClientService(
                clients,
                HardeningProxy.Create<IApiResourceRepository>((_, _) => null),
                HardeningProxy.Create<IOAuthClientSecretRepository>((_, _) => null),
                HardeningProxy.Create<IClientApiResourceRepository>((_, _) => null),
                HardeningProxy.Create<IRefreshTokenRepository>((_, _) => null),
                HardeningProxy.Create<IAuthorizationCodeRepository>((_, _) => null),
                HardeningProxy.Create<IDeviceAuthorizationRepository>((_, _) => null),
                serviceProvider,
                NullLogger<ClientService>.Instance);
        }

        public ClientService Service { get; }

        public int ClientLookups { get; private set; }

        public void Dispose() => serviceProvider.Dispose();
    }
}
