using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private const string TestBearerScheme = "Workbench.Tests.Bearer";

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
        var allowedClientIds = new HashSet<string>(
            [normalizedClientId, "additional-bearer-client"],
            StringComparer.Ordinal);
        var tokenClient = new TestTokenClient(normalizedClientId);

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
                Replace(services, RecordingDispatchProxy.Create<ITokenBlacklistRepository>((_, _) => false));
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
                services.AddSingleton(new TestBearerAuthenticationState(signingKey, allowedClientIds));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestBearerScheme;
                        options.DefaultChallengeScheme = TestBearerScheme;
                        options.DefaultForbidScheme = TestBearerScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestBearerAuthenticationHandler>(
                        TestBearerScheme,
                        _ => { });
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
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("name", users[userId].Nickname),
            new("token_use", "access_token"),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        };
        if (clientId is not null)
            claims.Add(new Claim("client_id", clientId));

        var token = new JwtSecurityToken(
            Authority,
            Audience,
            claims,
            now.AddSeconds(-1),
            now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    public void ResetUserLookups() => Interlocked.Exchange(ref userLookupCount, 0);

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

    private sealed record TestBearerAuthenticationState(
        SecurityKey SigningKey,
        IReadOnlySet<string> AllowedClientIds);

    private sealed class TestBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestBearerAuthenticationState state,
        IUserTokenValidator tokenValidator)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorizationValues))
            {
                var cookie = await Context.AuthenticateAsync(WorkbenchAuthenticationDefaults.CookieScheme);
                if (!cookie.Succeeded)
                    return AuthenticateResult.NoResult();

                return AuthenticateResult.Success(
                    new AuthenticationTicket(cookie.Principal!, Scheme.Name));
            }

            if (!AuthenticationHeaderValue.TryParse(authorizationValues.ToString(), out var header)
                || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(header.Parameter))
                return AuthenticateResult.Fail("A bearer token is required.");

            ClaimsPrincipal validated;
            try
            {
                validated = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(
                    header.Parameter,
                    new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = Authority,
                        ValidateAudience = true,
                        ValidAudience = Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = state.SigningKey,
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                        ClockSkew = TimeSpan.FromSeconds(30),
                        NameClaimType = "name",
                        RoleClaimType = "role",
                    },
                    out _);
            }
            catch (Exception)
            {
                return AuthenticateResult.Fail("The bearer token is invalid.");
            }

            if (!string.Equals(validated.FindFirst("token_use")?.Value, "access_token", StringComparison.Ordinal))
                return AuthenticateResult.Fail("Only access tokens are accepted.");

            var clientId = validated.FindFirst("client_id")?.Value;
            if (string.IsNullOrWhiteSpace(clientId) || !state.AllowedClientIds.Contains(clientId))
                return AuthenticateResult.Fail("The token client is not allowed.");

            if (!await tokenValidator.IsActiveAsync(validated, Context.RequestAborted))
                return AuthenticateResult.Fail("The token is inactive.");

            var subject = validated.FindFirst("sub")?.Value
                ?? validated.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(subject))
                return AuthenticateResult.Fail("The token subject is missing.");

            var claims = validated.Claims.ToList();
            if (validated.FindFirst("sub") is null)
                claims.Add(new Claim("sub", subject));
            if (validated.FindFirst(ClaimTypes.NameIdentifier) is null)
                claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                Scheme.Name,
                "name",
                "role"));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            // Preserve the fixture's existing Swagger browser redirect while API endpoints stay 401.
            if (Request.Path.StartsWithSegments("/swagger"))
                Response.Redirect($"{Request.Scheme}://{Request.Host}/api/auth/login");
            else
                Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    }

    private sealed class TestTokenClient(string clientId) : IWorkbenchTokenClient
    {
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

        public Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
