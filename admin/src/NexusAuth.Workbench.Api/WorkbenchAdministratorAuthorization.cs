using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api;

public static class WorkbenchAuthorizationPolicies
{
    public const string Administrator = "WorkbenchAdministrator";
}

public sealed class WorkbenchAdministratorRequirement : IAuthorizationRequirement;

public sealed class WorkbenchAdministratorAuthorizationHandler(IUserRepository users)
    : AuthorizationHandler<WorkbenchAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkbenchAdministratorRequirement requirement)
    {
        if (await IsAdministratorAsync(
                context.User,
                users,
                context.Resource as HttpContext,
                CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }

    internal static async Task<bool> IsAdministratorAsync(
        ClaimsPrincipal principal,
        IUserRepository users,
        HttpContext? httpContext,
        CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return false;

        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId))
            return false;

        var cancellationToken = httpContext?.RequestAborted ?? ct;
        return await users.FindByIdAsync(userId, cancellationToken) is
        {
            IsActive: true,
            IsSystemAccount: true,
        };
    }
}
