using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using NexusAuth.Workbench.Api.Controllers;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchAdministrationAuthorizationTests(
    WorkbenchAdministrationAuthorizationFixture fixture)
    : IClassFixture<WorkbenchAdministrationAuthorizationFixture>
{
    public static IEnumerable<object[]> ManagementReadEndpoints()
    {
        yield return ["/api/api-resources/all"];
        yield return ["/api/client-metadata"];
        yield return ["/api/clients/all"];
        yield return ["/api/login-audits"];
        yield return ["/api/open-api-credentials"];
        yield return ["/api/scim-credentials"];
        yield return [$"/api/users/{WorkbenchAdministrationAuthorizationFixture.TargetUserId}/passkeys"];
        yield return ["/api/users"];
    }

    [Fact]
    public void Management_controllers_rely_on_the_global_fallback_without_fixed_policy_names()
    {
        var managementControllers = typeof(WorkbenchApiModule).Assembly.GetTypes()
            .Where(type => !type.IsAbstract
                && type.Namespace == typeof(UsersController).Namespace
                && typeof(ControllerBase).IsAssignableFrom(type)
                && type != typeof(AuthController)
                && type != typeof(CurrentIdentityController))
            .ToArray();

        Assert.Equal(8, managementControllers.Length);
        Assert.All(managementControllers, type =>
        {
            Assert.Empty(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
            Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.All(type.GetMethods().Where(method => method.DeclaringType == type), method =>
            {
                Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
                Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            });
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_and_fallback_protect_new_endpoints_without_a_named_policy(bool gatewayEnabled)
    {
        using var configured = new WorkbenchAdministrationAuthorizationFixture("nexusauth.workbench", gatewayEnabled);
        using var anonymous = configured.CreateAnonymousClient();
        using var ordinary = configured.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);
        using var administrator = configured.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        foreach (var path in new[] { "/__test/access/default", "/__test/access/fallback" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/__test/access/public")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ordinary.GetAsync("/__test/access/named")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await administrator.GetAsync("/__test/access/named")).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Hosts_can_replace_the_framework_handler_or_policy_provider_without_controller_changes(
        bool replaceProvider, bool gatewayEnabled)
    {
        using var configured = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", gatewayEnabled, services =>
            {
                if (replaceProvider)
                    services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, TestWorkbenchPolicyProvider>());
                else
                {
                    // Keep the framework handlers (including RequireAuthenticatedUser) intact.
                    foreach (var descriptor in services.Where(descriptor =>
                        descriptor.ServiceType == typeof(IAuthorizationHandler)
                        && descriptor.ImplementationType == typeof(WorkbenchAccessAuthorizationHandler)).ToArray())
                    {
                        services.Remove(descriptor);
                    }
                    services.AddScoped<IAuthorizationHandler, TestWorkbenchAccessAuthorizationHandler>();
                }
            });
        using var anonymous = configured.CreateAnonymousClient();
        using var ordinary = configured.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);
        using var administrator = configured.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        foreach (var path in new[]
        {
            "/__test/access/default", "/__test/access/fallback", "/api/users",
            $"/api/users/{WorkbenchAdministrationAuthorizationFixture.TargetUserId}/passkeys",
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ordinary.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await administrator.GetAsync(path)).StatusCode);
        }

        using var me = await ordinary.GetAsync("/api/auth/me");
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isAdministrator").GetBoolean());
        using var deniedMe = await administrator.GetAsync("/api/auth/me");
        using var deniedBody = JsonDocument.Parse(await deniedMe.Content.ReadAsStringAsync());
        Assert.False(deniedBody.RootElement.GetProperty("isAdministrator").GetBoolean());

        // Write actions must also use the replacement, including both formerly hardcoded controllers.
        var userPath = $"/api/users/{WorkbenchAdministrationAuthorizationFixture.TargetUserId}";
        Assert.Equal(HttpStatusCode.NoContent, (await ordinary.DeleteAsync(userPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await administrator.DeleteAsync(userPath)).StatusCode);
        var revokePath = userPath + $"/passkeys/{Guid.NewGuid()}/revoke";
        Assert.Equal(HttpStatusCode.OK, (await ordinary.PostAsJsonAsync(revokePath, new { reason = "Lost device" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await administrator.PostAsJsonAsync(revokePath, new { reason = "Lost device" })).StatusCode);
    }

    [Fact]
    public async Task Ordinary_cookie_users_can_logout_without_management_access()
    {
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/auth/logout", null)).StatusCode);
        using var client = fixture.CreateAnonymousClient();
        await client.GetAsync($"/__test/sign-in/{WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(ManagementReadEndpoints))]
    public async Task Management_read_endpoints_enforce_administrator_access_through_the_http_pipeline(string path)
    {
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

        using var ordinary = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync(path)).StatusCode);

        using var administrator = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("nexusauth.workbench", true)]
    [InlineData("additional-bearer-client", true)]
    [InlineData("untrusted-client", false)]
    [InlineData(null, false)]
    public async Task Bearer_tokens_require_the_default_or_explicitly_allowed_client_id(
        string? clientId,
        bool accepted)
    {
        fixture.ResetUserLookups();
        using var client = fixture.CreateBearerClient(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId,
            clientId);

        using var response = await client.GetAsync("/api/users");

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(accepted, fixture.UserLookupCount > 0);
    }

    [Theory]
    [InlineData("nexusauth.workbench", false)]
    [InlineData("  nexusauth.workbench  ", false)]
    [InlineData("nexusauth.workbench", true)]
    public async Task Bearer_client_validation_uses_normalized_configuration_in_both_modes(
        string configuredClientId,
        bool gatewayEnabled)
    {
        using var configuredFixture = new WorkbenchAdministrationAuthorizationFixture(configuredClientId, gatewayEnabled);
        using var allowed = configuredFixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        using var additional = configuredFixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId, "additional-bearer-client");
        using var denied = configuredFixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.AdministratorId, "untrusted-client");
        using var allowedResponse = await allowed.GetAsync("/api/users");
        using var additionalResponse = await additional.GetAsync("/api/users");
        using var deniedResponse = await denied.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, additionalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedResponse.StatusCode);
    }

    [Fact]
    public async Task Inactive_administrator_bearer_token_is_rejected_during_authentication()
    {
        using var client = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.InactiveUserId);
        using var response = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Me_reports_administrator_status_for_authenticated_bearer_users(bool administrator)
    {
        using var client = fixture.CreateBearerClient(
            administrator
                ? WorkbenchAdministrationAuthorizationFixture.AdministratorId
                : WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);

        using var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(administrator, body.RootElement.GetProperty("isAdministrator").GetBoolean());
    }

    [Theory]
    [InlineData("administrator", HttpStatusCode.OK, true)]
    [InlineData("ordinary", HttpStatusCode.Forbidden, false)]
    [InlineData("inactive", HttpStatusCode.Forbidden, false)]
    [InlineData("missing", HttpStatusCode.Forbidden, false)]
    public async Task Cookie_sessions_remain_supported_and_policy_rejects_non_administrators(
        string account,
        HttpStatusCode managementStatus,
        bool expectedAdministrator)
    {
        var userId = account switch
        {
            "administrator" => WorkbenchAdministrationAuthorizationFixture.AdministratorId,
            "ordinary" => WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId,
            "inactive" => WorkbenchAdministrationAuthorizationFixture.InactiveUserId,
            _ => Guid.NewGuid(),
        };
        using var client = fixture.CreateAnonymousClient();
        using var signIn = await client.GetAsync($"/__test/sign-in/{userId}");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        using var management = await client.GetAsync("/api/users");
        Assert.Equal(managementStatus, management.StatusCode);

        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(expectedAdministrator, body.RootElement.GetProperty("isAdministrator").GetBoolean());
    }

    [Fact]
    public async Task Auth_routes_keep_their_existing_anonymous_and_non_administrator_behavior()
    {
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/auth/config")).StatusCode);

        using var me = await anonymous.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isAuthenticated").GetBoolean());
        Assert.False(body.GetProperty("isAdministrator").GetBoolean());

        using var ordinary = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);
        Assert.Equal(HttpStatusCode.OK, (await ordinary.GetAsync("/api/auth/me")).StatusCode);
    }
}

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

[ApiController]
public sealed class TestSessionController : ControllerBase
{
    [HttpGet("/__test/sign-in/{userId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn(Guid userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "Test Cookie User"),
            ],
            WorkbenchAuthenticationDefaults.CookieScheme));
        var properties = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = $"cookie-access:{userId}" },
            new AuthenticationToken { Name = "refresh_token", Value = "cookie-refresh" },
            new AuthenticationToken { Name = "id_token", Value = "cookie-id" },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture),
            },
        ]);

        await HttpContext.SignInAsync(
            WorkbenchAuthenticationDefaults.CookieScheme,
            principal,
            properties);
        return Ok();
    }
}

// Test-only controllers prove policy selection through endpoint routing and authorization middleware.
[ApiController]
public sealed class TestWorkbenchAccessController : ControllerBase
{
    [HttpGet("/__test/access/fallback")]
    public IActionResult Fallback() => Ok();

    [HttpGet("/__test/access/default")]
    [Authorize]
    public IActionResult Default() => Ok();

    [HttpGet("/__test/access/public")]
    [AllowAnonymous]
    public IActionResult Public() => Ok();

    [HttpGet("/__test/access/named")]
    [Authorize(Policy = "test-named")]
    public IActionResult Named() => Ok();
}

public sealed class TestWorkbenchAccessAuthorizationHandler : AuthorizationHandler<WorkbenchAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkbenchAccessRequirement requirement)
    {
        if (context.User.FindFirst("sub")?.Value == WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString())
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class TestWorkbenchPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider namedPolicies = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => namedPolicies.GetPolicyAsync(policyName);

    private static readonly AuthorizationPolicy Policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim("sub", WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString())
        .Build();

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(Policy);
}
