using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Clients;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ClientServiceHardeningTests
{
    private const string TokenEndpoint = "https://issuer.example/connect/token";
    private const string Issuer = "https://issuer.example";
    private const string ValidChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public async Task Scope_validation_rejects_scopes_spanning_multiple_resource_audiences()
    {
        var client = OAuthClient.Create("api-client", "API", allowedScopes: ["openid", "profile", "orders.api", "billing.api"]);
        var harness = new ClientServiceHarness(client);
        harness.Resources.Add(ApiResource.Create("orders", "Orders", "orders.api"));
        harness.Resources.Add(ApiResource.Create("billing", "Billing", "billing.api"));

        var multiple = await harness.Service.ValidateScopesAsync("api-client", "openid orders.api billing.api", allowIdentityScopes: true);
        var multipleMachine = await harness.Service.ValidateScopesAsync("api-client", "orders.api billing.api", allowIdentityScopes: false);
        var single = await harness.Service.ValidateScopesAsync("api-client", "openid profile orders.api", allowIdentityScopes: true);

        Assert.False(multiple.IsSuccess);
        Assert.Equal("invalid_scope", multiple.ErrorCode);
        Assert.Contains("multiple audiences", multiple.Error);
        Assert.False(multipleMachine.IsSuccess);
        Assert.Equal("invalid_scope", multipleMachine.ErrorCode);
        Assert.True(single.IsSuccess);
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM")]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM=")]
    public async Task Authorize_validation_rejects_malformed_code_challenge(string codeChallenge)
    {
        var client = OAuthClient.Create("web-client", "Web", redirectUris: ["https://client.example/callback"], allowedGrantTypes: ["authorization_code"]);
        var harness = new ClientServiceHarness(client);

        var result = await harness.Service.ValidateClientForAuthorizationAsync(
            "web-client", "https://client.example/callback", "authorization_code", codeChallenge, "S256");

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_request", result.ErrorCode);
        Assert.True(result.RedirectUriValidated);
    }

    [Fact]
    public async Task Authorize_validation_accepts_a_well_formed_s256_code_challenge()
    {
        var client = OAuthClient.Create("web-client", "Web", redirectUris: ["https://client.example/callback"], allowedGrantTypes: ["authorization_code"]);
        var harness = new ClientServiceHarness(client);

        var result = await harness.Service.ValidateClientForAuthorizationAsync(
            "web-client", "https://client.example/callback", "authorization_code", ValidChallenge, "S256");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Generating_credentials_is_capped_at_three_active_shared_secrets()
    {
        var client = OAuthClient.Create("secret-client", "Secrets", allowedGrantTypes: ["client_credentials"]);
        var harness = new ClientServiceHarness(client);

        for (var index = 0; index < ClientService.MaxActiveSharedSecrets; index++)
            await harness.Service.GenerateCredentialAsync(client.Id, new GenerateClientCredentialRequest());

        Assert.Equal(ClientService.MaxActiveSharedSecrets, client.ClientSecrets.Count(secret => secret.IsActive));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GenerateCredentialAsync(client.Id, new GenerateClientCredentialRequest()));
        Assert.Contains("at most 3 active shared secrets", exception.Message);
        Assert.Equal(ClientService.MaxActiveSharedSecrets, client.ClientSecrets.Count);
    }

    [Fact]
    public async Task Deleting_a_client_invalidates_its_grants_before_removing_it()
    {
        var client = OAuthClient.Create("doomed-client", "Doomed");
        var harness = new ClientServiceHarness(client);

        await harness.Service.DeleteAsync(client.Id);

        Assert.Equal(
            [
                "RevokeAllForClientAsync:doomed-client",
                "InvalidateUnusedForClientAsync:doomed-client",
                "DenyOutstandingForClientAsync:doomed-client",
                $"RemoveByClientIdAsync:{client.Id}",
                "DeleteAsync:doomed-client",
            ],
            harness.Calls);
    }

    [Fact]
    public async Task Deactivating_a_client_revokes_its_refresh_tokens()
    {
        var client = OAuthClient.Create("web-client", "Web");
        var harness = new ClientServiceHarness(client);

        await harness.Service.UpdateAsync(client.Id, Update(isActive: true));
        Assert.DoesNotContain("RevokeAllForClientAsync:web-client", harness.Calls);

        await harness.Service.UpdateAsync(client.Id, Update(isActive: false));
        Assert.Contains("RevokeAllForClientAsync:web-client", harness.Calls);
    }

    [Fact]
    public async Task Resetting_credentials_revokes_refresh_tokens()
    {
        var client = OAuthClient.Create("web-client", "Web");
        var harness = new ClientServiceHarness(client);

        await harness.Service.ResetCredentialAsync(client.Id, new GenerateClientCredentialRequest());

        Assert.Contains("RevokeAllForClientAsync:web-client", harness.Calls);
    }

    [Fact]
    public async Task Creating_a_client_with_jwks_uri_is_rejected()
    {
        var harness = new ClientServiceHarness(null);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.CreateAsync(new CreateClientRequest(
            "pk-client",
            "Private key client",
            null,
            null,
            null,
            null,
            ["client_credentials"],
            false,
            OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            false,
            null,
            "https://client.example/jwks.json")));

        Assert.Contains("jwks_uri is not supported yet", exception.Message);
        Assert.DoesNotContain(harness.Calls, call => call.StartsWith("AddAsync", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TokenEndpoint, true)]
    [InlineData(Issuer, true)]
    [InlineData("https://evil.example/connect/token", false)]
    [InlineData("http://issuer.example/connect/token", false)]
    public async Task Private_key_jwt_accepts_only_issuer_derived_audiences(string audience, bool expected)
    {
        using var rsa = RSA.Create(2048);
        var client = OAuthClient.Create(
            "pk-client",
            "Private key client",
            allowedGrantTypes: ["client_credentials"],
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: PublicJwks(rsa));
        var harness = new ClientServiceHarness(client);
        var input = new ClientAuthenticationInput(
            "pk-client",
            null,
            OAuthClient.ClientAssertionTypeJwtBearer,
            Assertion(rsa, "pk-client", audience),
            TokenEndpoint,
            null,
            [TokenEndpoint, Issuer]);

        var result = await harness.Service.AuthenticateClientAsync(input, requireClientAuthentication: true);

        Assert.Equal(expected, result.IsSuccess);
    }

    [Fact]
    public void Malformed_stored_jwks_is_an_authentication_failure_not_an_exception()
    {
        using var rsa = RSA.Create(2048);
        var client = OAuthClient.Create(
            "pk-client",
            "Private key client",
            tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodPrivateKeyJwt,
            jwks: PublicJwks(rsa));
        // Legacy rows were stored before JWKS validation existed.
        typeof(OAuthClient).GetProperty(nameof(OAuthClient.Jwks))!.SetValue(client, "{not-json");

        var result = ClientPrivateKeyJwtValidator.Validate(Assertion(rsa, "pk-client", TokenEndpoint), client, [TokenEndpoint]);

        Assert.False(result.IsSuccess);
        Assert.Equal("Client JWK set format is invalid.", result.Error);
    }

    private static UpdateClientRequest Update(bool? isActive) =>
        new(null, null, null, null, null, null, null, null, null, null, isActive);

    internal static string PublicJwks(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = "client-key",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }

    internal static string Assertion(RSA rsa, string clientId, string audience)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: clientId,
            audience: audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ],
            notBefore: now,
            expires: now.AddMinutes(2),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "client-key" }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class ClientServiceHarness
    {
        public ClientServiceHarness(OAuthClient? client)
        {
            var clients = HardeningProxy.Create<IOAuthClientRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IOAuthClientRepository.FindByClientIdAsync):
                        return client is not null && client.ClientId == (string)args![0]! ? client : null;
                    case nameof(IOAuthClientRepository.GetByIdAsync):
                        return client is not null && client.Id == (Guid)args![0]! ? client : null;
                    case nameof(IOAuthClientRepository.DeleteAsync):
                        Calls.Add($"DeleteAsync:{((OAuthClient)args![0]!).ClientId}");
                        return null;
                    case nameof(IOAuthClientRepository.AddAsync):
                        Calls.Add("AddAsync");
                        return null;
                    default:
                        return null;
                }
            });
            var resources = HardeningProxy.Create<IApiResourceRepository>((method, args) => method.Name switch
            {
                nameof(IApiResourceRepository.FindByAudienceAsync) => Resources.FirstOrDefault(resource => resource.Audience == (string)args![0]!),
                nameof(IApiResourceRepository.FindByIdsAsync) => (IReadOnlyList<ApiResource>)[],
                _ => null,
            });
            var secrets = HardeningProxy.Create<IOAuthClientSecretRepository>((method, args) =>
            {
                if (method.Name == nameof(IOAuthClientSecretRepository.AddAsync) && client is not null)
                    client.ClientSecrets.Add((OAuthClientSecret)args![0]!);
                return null;
            });
            var associations = HardeningProxy.Create<IClientApiResourceRepository>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IClientApiResourceRepository.GetResourcesByClientIdAsync):
                        return (IReadOnlyList<ApiResource>)[];
                    case nameof(IClientApiResourceRepository.RemoveByClientIdAsync):
                        Calls.Add($"RemoveByClientIdAsync:{args![0]}");
                        return null;
                    default:
                        return null;
                }
            });
            var refreshTokens = HardeningProxy.Create<IRefreshTokenRepository>((method, args) =>
            {
                if (method.Name == nameof(IRefreshTokenRepository.RevokeAllForClientAsync))
                    Calls.Add($"RevokeAllForClientAsync:{args![0]}");
                return null;
            });
            var codes = HardeningProxy.Create<IAuthorizationCodeRepository>((method, args) =>
            {
                if (method.Name == nameof(IAuthorizationCodeRepository.InvalidateUnusedForClientAsync))
                    Calls.Add($"InvalidateUnusedForClientAsync:{args![0]}");
                return null;
            });
            var devices = HardeningProxy.Create<IDeviceAuthorizationRepository>((method, args) =>
            {
                if (method.Name == nameof(IDeviceAuthorizationRepository.DenyOutstandingForClientAsync))
                    Calls.Add($"DenyOutstandingForClientAsync:{args![0]}");
                return null;
            });
            var blacklist = HardeningProxy.Create<ITokenBlacklistRepository>((_, _) => null);

            Service = new ClientService(
                clients,
                resources,
                blacklist,
                secrets,
                associations,
                refreshTokens,
                codes,
                devices,
                NullLogger<ClientService>.Instance);
        }

        public ClientService Service { get; }

        public List<ApiResource> Resources { get; } = [];

        public List<string> Calls { get; } = [];
    }
}
