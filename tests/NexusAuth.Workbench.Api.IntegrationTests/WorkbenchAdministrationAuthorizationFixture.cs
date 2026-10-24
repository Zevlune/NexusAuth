using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey signingKey;
    private readonly WebApplicationFactory<WorkbenchApiModule> factory;
    private readonly Dictionary<Guid, User> users;
    private int userLookupCount;

    public WorkbenchAdministrationAuthorizationFixture() : this(ClientId, false)
    {
    }

    internal WorkbenchAdministrationAuthorizationFixture(
        string configuredClientId, bool gatewayEnabled, Action<IServiceCollection>? configureAuthorization = null)
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
        factory = new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing")
                .UseSetting("Auth:Authority", Authority)
                .UseSetting("Auth:Audience", Audience)
                .UseSetting("Auth:ClientId", configuredClientId)
                .UseSetting("Gateway:Enabled", gatewayEnabled.ToString())
                .UseSetting("Auth:ClientSecret", "test-only-secret")
                .UseSetting("Auth:RedirectUri", "https://dashboard.example.test/signin-oidc")
                .UseSetting("Auth:PostLogoutRedirectUri", "https://dashboard.example.test/")
                .UseSetting("Auth:Scope", "openid profile offline_access nexusauth.workbench.api")
                .UseSetting("Auth:AllowedBearerClientIds:0", "additional-bearer-client");

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
                Replace(services, RecordingDispatchProxy.Create<IOidcWorkbenchService>((method, args) =>
                    method.Name switch
                    {
                        "get_Authority" => Authority,
                        "get_ClientId" => ClientId,
                        "get_RedirectUri" => "https://dashboard.example.test/signin-oidc",
                        "get_PostLogoutRedirectUri" => "https://dashboard.example.test/",
                        "get_Scope" => "openid profile offline_access nexusauth.workbench.api",
                        "get_SignOutProvider" => false,
                        nameof(IOidcWorkbenchService.IntrospectAccessTokenAsync) =>
                            new AccessTokenIntrospectionResult(
                                true,
                                ((string)args![0]!)["cookie-access:".Length..],
                                ClientId),
                        _ => null,
                    }));

                services.AddControllers().AddApplicationPart(typeof(TestSessionController).Assembly);
                services.AddAuthorization(options => options.AddPolicy("test-named", policy => policy
                    .RequireAuthenticatedUser().RequireClaim("sub", OrdinaryUserId.ToString())));
                configureAuthorization?.Invoke(services);
                services.PostConfigure<JwtBearerOptions>(
                    WorkbenchAuthenticationDefaults.BearerScheme,
                    options =>
                    {
                        var configuration = new OpenIdConnectConfiguration { Issuer = Authority };
                        configuration.SigningKeys.Add(signingKey);
                        options.Configuration = configuration;
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });
            });
        });
    }

    public int UserLookupCount => Volatile.Read(ref userLookupCount);

    public HttpClient CreateAnonymousClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

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

}
