using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api.Authorization;

/// <summary>Evaluates Workbench access through the framework's IAuthorizationHandler extension point.</summary>
public sealed class WorkbenchAccessAuthorizationHandler(IUserRepository users)
    : AuthorizationHandler<WorkbenchAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkbenchAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var subject = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId))
            return;

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        if (await users.FindByIdAsync(userId, cancellationToken) is { IsActive: true, IsSystemAccount: true })
            context.Succeed(requirement);
    }
}
