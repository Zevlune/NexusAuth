using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NexusAuth.Workbench.Api.Authorization;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchAccessAuthorizationHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Applies_the_service_decision_without_implementing_permission_rules(bool allowed)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/custom/resource";
        var context = CreateContext(httpContext);
        var calls = 0;
        var permissions = RecordingDispatchProxy.Create<IWorkbenchPermissionService>((_, args) =>
        {
            calls++;
            Assert.Same(context.User, args![0]);
            Assert.Same(httpContext, args[1]);
            return allowed;
        });
        var handler = new WorkbenchAccessAuthorizationHandler(permissions, new HttpContextAccessor());

        await handler.HandleAsync(context);

        Assert.Equal(allowed, context.HasSucceeded);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("middleware")]
    [InlineData("mvc")]
    [InlineData("accessor")]
    public async Task Resolves_request_context_and_passes_the_principal_and_cancellation_to_the_service(string source)
    {
        using var cancellation = new CancellationTokenSource();
        var httpContext = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var accessor = new HttpContextAccessor();
        object? resource = source switch
        {
            "middleware" => httpContext,
            "mvc" => new AuthorizationFilterContext(
                new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []),
            _ => null,
        };
        if (source == "accessor")
            accessor.HttpContext = httpContext;
        try
        {
            var context = CreateContext(resource);
            var calls = 0;
            var permissions = RecordingDispatchProxy.Create<IWorkbenchPermissionService>((_, args) =>
            {
                calls++;
                Assert.Same(context.User, args![0]);
                Assert.Same(httpContext, args[1]);
                Assert.Equal(cancellation.Token, args[2]);
                return true;
            });
            var handler = new WorkbenchAccessAuthorizationHandler(permissions, accessor);

            await handler.HandleAsync(context);

            Assert.True(context.HasSucceeded);
            Assert.Equal(1, calls);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task Missing_request_is_forwarded_to_the_service_for_rejection_and_logging()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        var context = CreateContext(null);
        var calls = 0;
        var permissions = RecordingDispatchProxy.Create<IWorkbenchPermissionService>((_, args) =>
        {
            calls++;
            Assert.Same(context.User, args![0]);
            Assert.Null(args[1]);
            Assert.Equal(CancellationToken.None, args[2]);
            return false;
        });
        var handler = new WorkbenchAccessAuthorizationHandler(permissions, accessor);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Service_failure_never_satisfies_the_requirement()
    {
        var failure = new InvalidOperationException("Permission source is unavailable.");
        var permissions = RecordingDispatchProxy.Create<IWorkbenchPermissionService>((_, _) => throw failure);
        var handler = new WorkbenchAccessAuthorizationHandler(permissions, new HttpContextAccessor());
        var context = CreateContext(new DefaultHttpContext());

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(context));

        Assert.Same(failure, actual);
        Assert.False(context.HasSucceeded);
    }

    private static AuthorizationHandlerContext CreateContext(object? resource) =>
        new([new WorkbenchAccessRequirement()], new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString())], "test")), resource);
}
