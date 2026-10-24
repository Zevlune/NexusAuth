using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Workbench.Api.Authorization;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchRequestPermissionTests
{
    [Fact]
    public void Workbench_module_automatically_registers_the_permission_service_with_scoped_lifetime()
    {
        using var fixture = new WorkbenchAdministrationAuthorizationFixture();
        using var first = fixture.CreateScope();
        using var second = fixture.CreateScope();

        var permissions = first.ServiceProvider.GetRequiredService<IWorkbenchPermissionService>();

        Assert.IsType<MockWorkbenchPermissionService>(permissions);
        Assert.Same(permissions, first.ServiceProvider.GetRequiredService<IWorkbenchPermissionService>());
        Assert.NotSame(permissions, second.ServiceProvider.GetRequiredService<IWorkbenchPermissionService>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Mock_permissions_allow_only_granted_methods_and_routes_in_both_modes(
        bool gatewayEnabled, bool useCookie)
    {
        using var fixture = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", gatewayEnabled, services =>
                services.Configure<MockWorkbenchPermissionOptions>(options =>
                    options.UserPermissions[WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString()] =
                    [
                        new("GET", "/api/users"),
                        new("GET", "/api/users/{id:guid}/passkeys"),
                        new("GET", "/api/auth/me"),
                    ]));
        using var client = useCookie
            ? fixture.CreateAnonymousClient()
            : fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);
        if (useCookie)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
                $"/__test/sign-in/{WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users?page=2")).StatusCode);
        foreach (var userId in new[] { WorkbenchAdministrationAuthorizationFixture.TargetUserId, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/users/{userId}/passkeys")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/users/{userId}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/users", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/clients/all")).StatusCode);

        using var me = await client.GetAsync("/api/auth/me");
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isAdministrator").GetBoolean());
    }

    [Fact]
    public async Task Changing_the_mock_permission_source_takes_effect_on_the_next_request()
    {
        var grants = new List<WorkbenchPermission> { new("GET", "/api/users") };
        using var fixture = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", false, services =>
                services.Configure<MockWorkbenchPermissionOptions>(options =>
                    options.UserPermissions[WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString()] = grants));
        using var client = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
        grants.Clear();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
        grants.Add(new("GET", "/api/clients/all"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/clients/all")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Methods_and_paths_from_different_grants_cannot_be_combined()
    {
        using var fixture = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", false, services =>
                services.Configure<MockWorkbenchPermissionOptions>(options =>
                    options.UserPermissions[WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString()] =
                    [new("GET", "/api/users"), new("POST", "/api/clients")]));
        using var client = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/users", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/clients")).StatusCode);
    }

    [Fact]
    public async Task Permission_service_can_be_replaced_without_replacing_the_framework_handler()
    {
        var lookups = new List<Guid>();
        var permissions = RecordingDispatchProxy.Create<IWorkbenchPermissionService>((_, args) =>
        {
            var user = (ClaimsPrincipal)args![0]!;
            if (user.Identity?.IsAuthenticated != true)
                return false;

            lookups.Add(Guid.Parse(user.FindFirstValue("sub")!));
            var httpContext = (HttpContext)args[1]!;
            return httpContext.Request.Method == "GET" && httpContext.Request.Path == "/api/users";
        });
        using var fixture = new WorkbenchAdministrationAuthorizationFixture(
            "nexusauth.workbench", false, services =>
                services.Replace(ServiceDescriptor.Scoped<IWorkbenchPermissionService>(_ => permissions)));
        using var anonymous = fixture.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
        Assert.Empty(lookups);
        using var client = fixture.CreateBearerClient(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/clients/all")).StatusCode);
        Assert.Equal(2, lookups.Count);
        Assert.All(lookups, id => Assert.Equal(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId, id));
    }
}
