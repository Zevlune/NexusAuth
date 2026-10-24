using Microsoft.AspNetCore.Authorization;
using NexusAuth.Workbench.Api.Authorization;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class TestWorkbenchAccessAuthorizationHandler : AuthorizationHandler<WorkbenchAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkbenchAccessRequirement requirement)
    {
        if (context.User.FindFirst("sub")?.Value == WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId.ToString())
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
