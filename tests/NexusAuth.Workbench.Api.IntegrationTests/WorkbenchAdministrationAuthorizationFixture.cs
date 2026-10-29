using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Authentication.Tokens;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application;
using NexusAuth.Application.Clients;
using NexusAuth.Application.Services.ApiResources;
using NexusAuth.Application.Services.LoginAudits;
using NexusAuth.Application.Services.OpenApi;
using NexusAuth.Application.Services.Scim;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using NexusAuth.Workbench.Api;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchAdministrationAuthorizationFixture : IDisposable
{
    public static readonly Guid AdministratorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OrdinaryUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid InactiveUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid TargetUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string Authority = "https://issuer.example.test";
    private const string Audience = "nexusauth.workbench.api";
    private const string ClientId = "nexusauth.workbench";
    private const string WorkbenchScope = OidcScopes.OpenId + " " + OidcScopes.Profile
        + " " + OidcScopes.OfflineAccess + " " + Audience;
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey signingKey;
    private readonly WebApplicationFactory<WorkbenchApiModule> factory;
    private readonly Dictionary<Guid, User> users;
    private readonly HashSet<string> blacklistedJtis = new(StringComparer.Ordinal);
    private readonly TestTokenClient tokenClient;
    private int userLookupCount;

    public WorkbenchAdministrationAuthorizationFixture() : this(ClientId)
    {
    }

    internal WorkbenchAdministrationAuthorizationFixture(
        string configuredClientId, Action<IServiceCollection>? configureAuthorization = null)
    {
        var administrator = User.Create("administrator", "test-password", "Administrator");
        administrator.MarkAsSystemAccount();
        var ordinary = User.Create("ordinary", "test-password", "Ordinary User");
        var inactive = User.Create("inactive", "test-password", "Inactive User");
        inactive.UpdateScimProfile(inactive.Username, inactive.Nickname, isActive: false);
        inactive.MarkAsSystemAccount();
        var target = User.Create("target", "test-password", "Target User");

        users = new Dictionary<Guid, User>
        {
            [AdministratorId] = administrator,
            [OrdinaryUserId] = ordinary,
            [InactiveUserId] = inactive,
            [TargetUserId] = target,
        };

        signingKey = new RsaSecurityKey(rsa) { KeyId = "authorization-test" };
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = Authority,
            AuthorizationEndpoint = Authority + "/authorize",
            TokenEndpoint = Authority + "/token",
            EndSessionEndpoint = Authority + "/logout",
        };
        configuration.SigningKeys.Add(signingKey);
        var metadata = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        var normalizedClientId = configuredClientId.Trim();
        tokenClient = new TestTokenClient(normalizedClientId);

        factory = new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing")
                .UseSetting("Auth:Authority", Authority)
                .UseSetting("Auth:Audience", Audience)
                .UseSetting("Auth:ClientId", configuredClientId)
                .UseSetting("Auth:ClientSecret", "test-only-secret")
                .UseSetting("Auth:RedirectUri", "https://dashboard.example.test/signin-oidc")
                .UseSetting("Auth:PostLogoutRedirectUri", "https://dashboard.example.test/")
                .UseSetting("Auth:Scope", WorkbenchScope)
                .UseSetting("Auth:AllowedBearerClientIds:0", "additional-bearer-client")
                .UseSetting("Swagger:Enabled", "true");

            builder.ConfigureServices(services =>
            {
                Replace(services, RecordingDispatchProxy.Create<IUserRepository>((method, args) =>
                {
                    if (method.Name != nameof(IUserRepository.FindByIdAsync))
                        return null;

                    Interlocked.Increment(ref userLookupCount);
                    return users.GetValueOrDefault((Guid)args![0]!);
                }));
                Replace(services, RecordingDispatchProxy.Create<ITokenBlacklistRepository>((method, args) =>
                    method.Name == nameof(ITokenBlacklistRepository.ExistsActiveAsync)
                    && IsBlacklisted((string)args![0]!)));
                Replace(services, RecordingDispatchProxy.Create<IApiResourceService>((method, _) =>
                    method.Name == nameof(IApiResourceService.GetAllAsync)
                        ? new List<ApiResourceDto>()
                        : null));
                Replace(services, RecordingDispatchProxy.Create<IClientMetadataService>((_, _) =>
                    new ClientMetadataDto([], [], [])));
                Replace(services, RecordingDispatchProxy.Create<IClientService>((method, _) =>
                    method.Name == nameof(IClientService.GetAllAsync)
                        ? new List<ClientDto>()
                        : null));
                Replace(services, RecordingDispatchProxy.Create<ILoginAuditService>((method, _) =>
                    method.Name == nameof(ILoginAuditService.GetPagedAsync)
                        ? new PagedResult<LoginAuditLogDto>([], 0, 1, 10)
                        : null));
                Replace(services, RecordingDispatchProxy.Create<IOpenApiCredentialService>((method, _) =>
                    method.Name == nameof(IOpenApiCredentialService.GetAllAsync)
                        ? Array.Empty<OpenApiCredentialSummary>()
                        : null));
                Replace(services, RecordingDispatchProxy.Create<IScimCredentialService>((method, _) =>
                    method.Name == nameof(IScimCredentialService.GetAllAsync)
                        ? Array.Empty<ScimCredentialSummary>()
                        : null));
                Replace(services, RecordingDispatchProxy.Create<IPasskeyManagementService>((method, _) =>
                    method.Name switch
                    {
                        nameof(IPasskeyManagementService.GetAsync) => Array.Empty<WebAuthnCredentialSummary>(),
                        nameof(IPasskeyManagementService.RevokeAsync) => true,
                        _ => null,
                    }));
                Replace(services, RecordingDispatchProxy.Create<IUserManagementService>((method, _) =>
                    method.Name == nameof(IUserManagementService.GetPagedAsync)
                        ? new PagedResult<ManagedUserDto>([], 0, 1, 10)
                        : null));

                services.RemoveAll<IWorkbenchTokenClient>();
                services.AddScoped<IWorkbenchTokenClient>(_ => tokenClient);
                services.PostConfigure<JwtBearerOptions>(
                    WorkbenchAuthenticationDefaults.BearerScheme,
                    options => options.ConfigurationManager = metadata);
                services.PostConfigure<OpenIdConnectOptions>(
                    WorkbenchAuthenticationDefaults.OpenIdConnectScheme,
                    options => options.ConfigurationManager = metadata);

                services.AddControllers().AddApplicationPart(typeof(TestSessionController).Assembly);
                services.AddAuthorization(options => options.AddPolicy("test-named", policy => policy
                    .RequireAuthenticatedUser().RequireClaim("sub", OrdinaryUserId.ToString())));
                configureAuthorization?.Invoke(services);
            });
        });
    }

    public int UserLookupCount => Volatile.Read(ref userLookupCount);

    public HttpClient CreateAnonymousClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    public HttpClient CreateBearerClient(Guid userId, string? clientId = ClientId)
        => CreateClientWithBearerToken(CreateBearerToken(userId, clientId: clientId));

    public HttpClient CreateClientWithBearerToken(string token)
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public string CreateBearerToken(
        Guid userId,
        string? clientId = ClientId,
        string? tokenUse = "access_token",
        string? issuer = null,
        string? audience = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        SecurityKey? signingKeyOverride = null,
        string? jti = null)
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("name", users[userId].Nickname),
            new("iat", issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("jti", jti ?? Guid.NewGuid().ToString()),
        };
        if (tokenUse is not null)
            claims.Add(new Claim("token_use", tokenUse));
        if (clientId is not null)
            claims.Add(new Claim("client_id", clientId));

        var token = new JwtSecurityToken(
            issuer ?? Authority,
            audience ?? Audience,
            claims,
            (notBefore ?? issuedAt.AddSeconds(-1)).UtcDateTime,
            (expires ?? issuedAt.AddMinutes(5)).UtcDateTime,
            new SigningCredentials(
                signingKeyOverride ?? signingKey,
                SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void ResetUserLookups() => Interlocked.Exchange(ref userLookupCount, 0);

    public IReadOnlyList<(string Token, string TokenTypeHint)> RevokedTokens => tokenClient.RevokedTokens;

    public void ResetTokenRevocations() => tokenClient.ResetRevocations();

    public void BlacklistToken(string jti)
    {
        lock (blacklistedJtis)
            blacklistedJtis.Add(jti);
    }

    public void Dispose()
    {
        factory.Dispose();
        rsa.Dispose();
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    private bool IsBlacklisted(string jti)
    {
        lock (blacklistedJtis)
            return blacklistedJtis.Contains(jti);
    }

    private sealed class TestTokenClient(string clientId) : IWorkbenchTokenClient
    {
        private readonly List<(string Token, string TokenTypeHint)> revocations = [];
        private readonly object sync = new();

        public IReadOnlyList<(string Token, string TokenTypeHint)> RevokedTokens
        {
            get
            {
                lock (sync)
                    return revocations.ToArray();
            }
        }

        public void ResetRevocations()
        {
            lock (sync)
                revocations.Clear();
        }

        public Task<WorkbenchTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkbenchIntrospectionResult> IntrospectAccessTokenAsync(
            string accessToken,
            CancellationToken ct)
        {
            var subject = accessToken.StartsWith("cookie-access:", StringComparison.Ordinal)
                ? accessToken["cookie-access:".Length..]
                : null;
            return Task.FromResult(new WorkbenchIntrospectionResult(
                subject is not null,
                subject,
                subject is null ? null : clientId));
        }

        public Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct)
        {
            lock (sync)
                revocations.Add((token, tokenTypeHint));
            return Task.CompletedTask;
        }
    }
}
