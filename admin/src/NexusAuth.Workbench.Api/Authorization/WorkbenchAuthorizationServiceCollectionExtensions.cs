using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Workbench.Api.Authorization;

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
