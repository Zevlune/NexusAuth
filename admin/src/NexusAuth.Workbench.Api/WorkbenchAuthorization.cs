using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Workbench.Api;

public static class WorkbenchAuthorizationPolicies
{
    // Session operations remain available to authenticated users without management access.
    public const string AuthenticatedUser = "WorkbenchAuthenticatedUser";
}

public sealed class WorkbenchAccessRequirement : IAuthorizationRequirement;

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

public static class WorkbenchAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            var managementAccess = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new WorkbenchAccessRequirement())
                .Build();
            options.DefaultPolicy = managementAccess;
            options.FallbackPolicy = managementAccess;
            options.AddPolicy(WorkbenchAuthorizationPolicies.AuthenticatedUser,
                policy => policy.RequireAuthenticatedUser());
        });
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, WorkbenchAccessAuthorizationHandler>());
        return services;
    }
}
