using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NexusAuth.Workbench.Api.Authorization;

/// <summary>Evaluates Workbench access through the framework's IAuthorizationHandler extension point.</summary>
public sealed class WorkbenchAccessAuthorizationHandler(
    IWorkbenchPermissionService permissions,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<WorkbenchAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkbenchAccessRequirement requirement)
    {
        var httpContext = context.Resource switch
        {
            HttpContext requestContext => requestContext,
            AuthorizationFilterContext filterContext => filterContext.HttpContext,
            _ => httpContextAccessor.HttpContext,
        };
        if (await permissions.HasPermissionAsync(
                context.User, httpContext, httpContext?.RequestAborted ?? CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}
