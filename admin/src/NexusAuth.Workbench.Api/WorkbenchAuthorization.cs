using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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

/// <summary>Protects both bare [Authorize] and endpoints with no authorization metadata.</summary>
public sealed class WorkbenchAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider namedPolicies = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => namedPolicies.GetPolicyAsync(policyName);

    private static readonly AuthorizationPolicy ManagementAccess = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new WorkbenchAccessRequirement())
        .Build();

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(ManagementAccess);

    public async Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => await GetDefaultPolicyAsync();
}

public static class WorkbenchAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchAuthorization(this IServiceCollection services)
    {
        // MVC can register the framework provider before this module. Remove only that
        // built-in registration, preserving any provider explicitly supplied by the host.
        foreach (var descriptor in services.Where(descriptor =>
            descriptor.ServiceType == typeof(IAuthorizationPolicyProvider)
            && descriptor.ImplementationType == typeof(DefaultAuthorizationPolicyProvider)).ToArray())
        {
            services.Remove(descriptor);
        }
        services.TryAddSingleton<IAuthorizationPolicyProvider, WorkbenchAuthorizationPolicyProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, WorkbenchAccessAuthorizationHandler>());
        services.AddAuthorization(options => options.AddPolicy(
            WorkbenchAuthorizationPolicies.AuthenticatedUser,
            policy => policy.RequireAuthenticatedUser()));
        return services;
    }
}
