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

/// <summary>Decides whether an authenticated principal may access Workbench management APIs.</summary>
public interface IWorkbenchAccessEvaluator
{
    Task<bool> CanAccessAsync(
        ClaimsPrincipal principal,
        HttpContext? httpContext,
        CancellationToken ct = default);
}

/// <summary>The default management access rule; replace IWorkbenchAccessEvaluator to customize it.</summary>
public sealed class SystemAccountWorkbenchAccessEvaluator(IUserRepository users) : IWorkbenchAccessEvaluator
{
    public async Task<bool> CanAccessAsync(
        ClaimsPrincipal principal,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return false;

        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(subject, out var userId)
            && await users.FindByIdAsync(userId, ct) is { IsActive: true, IsSystemAccount: true };
    }
}

public sealed class WorkbenchAccessAuthorizationHandler(IWorkbenchAccessEvaluator access)
    : AuthorizationHandler<WorkbenchAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WorkbenchAccessRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext;
        if (context.User.Identity?.IsAuthenticated == true
            && await access.CanAccessAsync(context.User, httpContext, httpContext?.RequestAborted ?? CancellationToken.None))
        {
            context.Succeed(requirement);
        }
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
        services.TryAddScoped<IWorkbenchAccessEvaluator, SystemAccountWorkbenchAccessEvaluator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, WorkbenchAccessAuthorizationHandler>());
        services.AddAuthorization(options => options.AddPolicy(
            WorkbenchAuthorizationPolicies.AuthenticatedUser,
            policy => policy.RequireAuthenticatedUser()));
        return services;
    }
}
