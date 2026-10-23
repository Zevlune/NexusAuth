using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Clients.Authentication;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Shared.DependencyInjection;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public class ClientAuthenticationStrategyTests
{
    [Theory]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
        "PrivateKeyJwtAuthenticationStrategy")]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodClientSecretJwt,
        "ClientSecretJwtAuthenticationStrategy")]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodClientSecretBasic,
        "ClientSecretBasicAuthenticationStrategy")]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodClientSecretPost,
        "ClientSecretPostAuthenticationStrategy")]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodNone,
        "NoneAuthenticationStrategy")]
    public void Assembly_scanning_registers_each_supported_method_key(
        string method,
        string expectedTypeName)
    {
        var services = new ServiceCollection();
        var (blacklistRepository, _) = CreateBlacklistRepository();
        services.AddSingleton(blacklistRepository);
        services.AddScopeKeyedServices(typeof(ClientService).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var strategy = scope.ServiceProvider.ResolveKeyed<IClientAuthenticationStrategy>(method);

        Assert.NotNull(strategy);
        Assert.Equal(expectedTypeName, strategy.GetType().Name);
    }

    [Fact]
    public void Unregistered_key_resolves_to_null()
    {
        var services = new ServiceCollection();
        var (blacklistRepository, _) = CreateBlacklistRepository();
        services.AddSingleton(blacklistRepository);
        services.AddScopeKeyedServices(typeof(ClientService).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Null(
            scope.ServiceProvider.ResolveKeyed<IClientAuthenticationStrategy>("unsupported"));
    }

    [Theory]
    [InlineData(OAuthClient.TokenEndpointAuthMethodClientSecretBasic)]
    [InlineData(OAuthClient.TokenEndpointAuthMethodClientSecretPost)]
    public async Task Client_service_dispatches_shared_secret_methods(string method)
    {
        var (client, rawSecret) = CreateSharedSecretClient(method);
        using var strategyProvider = CreateStrategyProvider();
        var service = CreateClientService(client, strategyProvider);

        var result = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(client.ClientId, rawSecret, null, null, null, method),
            requireClientAuthentication: true);

        Assert.True(result.IsSuccess);
        Assert.Same(client, result.Client);
    }

    [Theory]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodClientSecretBasic,
        OAuthClient.TokenEndpointAuthMethodClientSecretPost,
        "Client must authenticate with client_secret_basic.")]
    [InlineData(
        OAuthClient.TokenEndpointAuthMethodClientSecretPost,
        OAuthClient.TokenEndpointAuthMethodClientSecretBasic,
        "Client must authenticate with client_secret_post.")]
    public async Task Shared_secret_strategies_preserve_method_errors(
        string clientMethod,
        string suppliedMethod,
        string expectedError)
    {
        var (client, rawSecret) = CreateSharedSecretClient(clientMethod);
        using var strategyProvider = CreateStrategyProvider();
        var service = CreateClientService(client, strategyProvider);

        var result = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(client.ClientId, rawSecret, null, null, null, suppliedMethod),
            requireClientAuthentication: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_client", result.ErrorCode);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public async Task Shared_secret_strategy_rejects_assertions_before_method_mismatch()
    {
        var (client, _) = CreateSharedSecretClient(OAuthClient.TokenEndpointAuthMethodClientSecretBasic);
        using var strategyProvider = CreateStrategyProvider();
        var service = CreateClientService(client, strategyProvider);

        var result = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(
                client.ClientId,
                null,
                OAuthClient.ClientAssertionTypeJwtBearer,
                "unused-assertion",
                null,
                OAuthClient.TokenEndpointAuthMethodClientSecretPost),
            requireClientAuthentication: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "This client requires client secret authentication.",
            result.Error);
    }

    [Fact]
    public async Task Public_client_without_required_authentication_still_succeeds()
    {
        var client = OAuthClient.Create(
            "public-client",
            "Public client",
            allowedGrantTypes: ["urn:nexusauth:grant-type:wechat_miniprogram"],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone);
        using var strategyProvider = CreateStrategyProvider();
        var service = CreateClientService(client, strategyProvider);

        var result = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(
                client.ClientId,
                "unused-secret",
                OAuthClient.ClientAssertionTypeJwtBearer,
                "unused-assertion"),
            requireClientAuthentication: false);

        Assert.True(result.IsSuccess);
        Assert.Same(client, result.Client);
    }

    [Fact]
    public async Task Public_client_required_authentication_preserves_legacy_errors()
    {
        var client = OAuthClient.Create(
            "public-client-required",
            "Public client",
            allowedGrantTypes: ["urn:nexusauth:grant-type:wechat_miniprogram"],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone);
        using var strategyProvider = CreateStrategyProvider();
        var service = CreateClientService(client, strategyProvider);

        var assertionResult = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(
                client.ClientId,
                null,
                OAuthClient.ClientAssertionTypeJwtBearer,
                "unused-assertion"),
            requireClientAuthentication: true);
        var missingCredentialResult = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(client.ClientId, null, null, null),
            requireClientAuthentication: true);
        var invalidCredentialResult = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(client.ClientId, "unused-secret", null, null),
            requireClientAuthentication: true);

        Assert.Equal(
            "This client requires client secret authentication.",
            assertionResult.Error);
        Assert.Equal("client_secret is required.", missingCredentialResult.Error);
        Assert.Equal("Invalid client secret.", invalidCredentialResult.Error);
    }

    [Fact]
    public async Task Multiple_credentials_are_rejected_before_strategy_dispatch()
    {
        var (client, rawSecret) = CreateSharedSecretClient(OAuthClient.TokenEndpointAuthMethodClientSecretPost);
        using var strategyProvider = CreateStrategyProvider();
        var service = CreateClientService(client, strategyProvider);

        var result = await service.AuthenticateClientAsync(
            new ClientAuthenticationInput(
                client.ClientId,
                rawSecret,
                OAuthClient.ClientAssertionTypeJwtBearer,
                "assertion",
                "https://issuer.example/token",
                OAuthClient.TokenEndpointAuthMethodClientSecretPost),
            requireClientAuthentication: true);

        Assert.False(result.IsSuccess);
        Assert.Equal("Multiple client authentication methods were supplied.", result.Error);
    }

    [Fact]
    public async Task Client_secret_jwt_validates_audience_and_blocks_replay()
    {
        const string rawSecret = "client-secret-jwt-value-0123456789";
        var clientId = Guid.NewGuid();
        var secret = OAuthClientSecret.CreateSharedSecret(
            clientId,
            rawSecret,
            persistPlainValue: true);
        var client = OAuthClient.Create(
            clientId,
            "client-secret-jwt-client",
            "Client secret JWT client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodClientSecretJwt,
            clientSecrets: [secret]);
        var (blacklistRepository, blacklistState) = CreateBlacklistRepository();
        using var serviceProvider = CreateStrategyProvider(blacklistRepository);
        var service = CreateClientService(client, serviceProvider);
        var assertion = CreateHmacJwt(
            client.ClientId,
            "https://issuer.example/token",
            "client-secret-jti",
            rawSecret);

        var wrongAudience = await service.AuthenticateClientAsync(
            CreateJwtInput(client.ClientId, assertion, "https://wrong.example/token"),
            requireClientAuthentication: true);
        Assert.False(wrongAudience.IsSuccess);
        Assert.Equal("invalid_client", wrongAudience.ErrorCode);

        var accepted = await service.AuthenticateClientAsync(
            CreateJwtInput(client.ClientId, assertion, "https://issuer.example/token"),
            requireClientAuthentication: true);
        Assert.True(accepted.IsSuccess, accepted.Error);
        Assert.Single(blacklistState.Entries);

        var replayed = await service.AuthenticateClientAsync(
            CreateJwtInput(client.ClientId, assertion, "https://issuer.example/token"),
            requireClientAuthentication: true);
        Assert.False(replayed.IsSuccess);
        Assert.Equal("client_assertion has already been used.", replayed.Error);
    }

    [Fact]
    public async Task Private_key_jwt_uses_registered_jwks_and_blocks_replay()
    {
        using var rsa = RSA.Create(2048);
        const string keyId = "private-key-jwt-key";
        var parameters = rsa.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = keyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                }
            }
        });
        var client = OAuthClient.Create(
            "private-key-jwt-client",
            "Private key JWT client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: jwks);
        var (blacklistRepository, _) = CreateBlacklistRepository();
        using var serviceProvider = CreateStrategyProvider(blacklistRepository);
        var service = CreateClientService(client, serviceProvider);
        var assertion = CreateRsaJwt(
            client.ClientId,
            "https://issuer.example/token",
            "private-key-jti",
            rsa,
            keyId);

        var accepted = await service.AuthenticateClientAsync(
            CreateJwtInput(client.ClientId, assertion, "https://issuer.example/token"),
            requireClientAuthentication: true);
        var replayed = await service.AuthenticateClientAsync(
            CreateJwtInput(client.ClientId, assertion, "https://issuer.example/token"),
            requireClientAuthentication: true);

        Assert.True(accepted.IsSuccess, accepted.Error);
        Assert.False(replayed.IsSuccess);
        Assert.Equal("client_assertion has already been used.", replayed.Error);
    }

    private static ClientAuthenticationInput CreateJwtInput(
        string clientId,
        string assertion,
        string audience)
    {
        return new ClientAuthenticationInput(
            clientId,
            null,
            OAuthClient.ClientAssertionTypeJwtBearer,
            assertion,
            audience);
    }

    private static (OAuthClient Client, string RawSecret) CreateSharedSecretClient(string method)
    {
        const string rawSecret = "shared-secret-value";
        var clientId = Guid.NewGuid();
        var secret = OAuthClientSecret.CreateSharedSecret(
            clientId,
            rawSecret,
            persistPlainValue: string.Equals(
                method,
                OAuthClient.TokenEndpointAuthMethodClientSecretJwt,
                StringComparison.Ordinal));
        var client = OAuthClient.Create(
            clientId,
            $"{method}-client",
            $"{method} client",
            tokenEndpointAuthMethod: method,
            clientSecrets: [secret]);
        return (client, rawSecret);
    }

    private static ClientService CreateClientService(
        OAuthClient client,
        IServiceProvider serviceProvider)
    {
        return new ClientService(
            CreateOAuthClientRepository(client),
            null!,
            null!,
            null!,
            serviceProvider,
            NullLogger<ClientService>.Instance);
    }

    private static ServiceProvider CreateStrategyProvider(ITokenBlacklistRepository? blacklistRepository = null)
    {
        if (blacklistRepository is null)
        {
            (blacklistRepository, _) = CreateBlacklistRepository();
        }

        var services = new ServiceCollection();
        services.AddSingleton(blacklistRepository);
        services.AddScopeKeyedServices(typeof(ClientService).Assembly);
        return services.BuildServiceProvider();
    }

    private static string CreateHmacJwt(
        string issuer,
        string audience,
        string jti,
        string secret)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        return WriteJwt(issuer, audience, jti, credentials);
    }

    private static string CreateRsaJwt(
        string issuer,
        string audience,
        string jti,
        RSA rsa,
        string keyId)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = keyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        return WriteJwt(issuer, audience, jti, credentials);
    }

    private static string WriteJwt(
        string issuer,
        string audience,
        string jti,
        SigningCredentials credentials)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, issuer),
                new Claim(JwtRegisteredClaimNames.Jti, jti),
            ],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static IOAuthClientRepository CreateOAuthClientRepository(OAuthClient client)
    {
        var repository = DispatchProxy.Create<IOAuthClientRepository, OAuthClientRepositoryProxy>();
        ((OAuthClientRepositoryProxy)(object)repository).Client = client;
        return repository;
    }

    private static (ITokenBlacklistRepository Repository, BlacklistState State) CreateBlacklistRepository()
    {
        var repository = DispatchProxy.Create<ITokenBlacklistRepository, TokenBlacklistRepositoryProxy>();
        var state = new BlacklistState();
        ((TokenBlacklistRepositoryProxy)(object)repository).State = state;
        return (repository, state);
    }

    private class OAuthClientRepositoryProxy : DispatchProxy
    {
        public OAuthClient? Client { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (string.Equals(targetMethod?.Name, nameof(IOAuthClientRepository.FindByClientIdAsync), StringComparison.Ordinal)
                && args is [string clientId, ..])
            {
                return Task.FromResult(
                    string.Equals(Client?.ClientId, clientId, StringComparison.Ordinal)
                        ? Client
                        : null);
            }

            throw new NotSupportedException($"Unexpected repository call: {targetMethod?.Name}");
        }
    }

    private class TokenBlacklistRepositoryProxy : DispatchProxy
    {
        public BlacklistState State { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (string.Equals(targetMethod?.Name, nameof(ITokenBlacklistRepository.ExistsActiveAsync), StringComparison.Ordinal)
                && args is [string jti, DateTimeOffset now, ..])
            {
                return Task.FromResult(
                    State.Entries.Any(entry =>
                        string.Equals(entry.Jti, jti, StringComparison.Ordinal)
                        && entry.ExpiresAt > now));
            }

            if (string.Equals(targetMethod?.Name, nameof(ITokenBlacklistRepository.AddAsync), StringComparison.Ordinal)
                && args is [TokenBlacklistEntry entry, ..])
            {
                State.Entries.Add(entry);
                return Task.CompletedTask;
            }

            throw new NotSupportedException($"Unexpected token blacklist call: {targetMethod?.Name}");
        }
    }

    private class BlacklistState
    {
        public List<TokenBlacklistEntry> Entries { get; } = [];
    }
}
