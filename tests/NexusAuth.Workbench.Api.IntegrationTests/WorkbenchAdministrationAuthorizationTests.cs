using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Domain.Repositories;
using NexusAuth.Workbench.Api;
using NexusAuth.Workbench.Api.Authorization;
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
    public void Management_controllers_use_default_or_fallback_without_fixed_policy_names()
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
            var authorization = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToArray();
            if (type == typeof(ApiResourcesController))
            {
                var attribute = Assert.Single(authorization);
                Assert.Null(attribute.Policy);
                Assert.Null(attribute.Roles);
                Assert.Null(attribute.AuthenticationSchemes);
            }
            else
            {
                Assert.Empty(authorization);
            }
            Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.All(type.GetMethods().Where(method => method.DeclaringType == type), method =>
            {
                Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
                Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            });
        });
    }

    [Fact]
    public void Workbench_uses_the_framework_policy_provider_and_authorization_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        services.AddWorkbenchAuthorization();
        services.AddScoped<IWorkbenchPermissionService, MockWorkbenchPermissionService>();
        services.AddScoped<IUserRepository>(_ => RecordingDispatchProxy.Create<IUserRepository>((_, _) => null));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DefaultAuthorizationPolicyProvider>(provider.GetRequiredService<IAuthorizationPolicyProvider>());
        Assert.IsAssignableFrom<DefaultAuthorizationService>(provider.GetRequiredService<IAuthorizationService>());
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.Same(options.DefaultPolicy, options.FallbackPolicy);
        Assert.Contains(options.DefaultPolicy.Requirements, requirement => requirement is WorkbenchAccessRequirement);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hosts_can_replace_the_framework_handler_without_controller_changes(bool gatewayEnabled)
    {
        using var configured = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", gatewayEnabled, services =>
            {
                // Keep the framework handlers (including RequireAuthenticatedUser) intact.
                foreach (var descriptor in services.Where(descriptor =>
                    descriptor.ServiceType == typeof(IAuthorizationHandler)
                    && descriptor.ImplementationType == typeof(WorkbenchAccessAuthorizationHandler)).ToArray())
                {
                    services.Remove(descriptor);
                }
                services.AddScoped<IAuthorizationHandler, TestWorkbenchAccessAuthorizationHandler>();
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
