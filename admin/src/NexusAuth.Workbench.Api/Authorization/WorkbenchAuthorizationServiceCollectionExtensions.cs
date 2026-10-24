using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Workbench.Api.Authorization;

public static class WorkbenchAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchAuthorization(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddHttpContextAccessor();
        ConfigureMockPermissions(services, configuration);
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

    private static void ConfigureMockPermissions(IServiceCollection services, IConfiguration? configuration)
    {
        var options = services.AddOptions<MockWorkbenchPermissionOptions>();
        if (configuration is null)
            return;

        var section = configuration.GetSection(MockWorkbenchPermissionOptions.SectionName);
        options.Configure(settings =>
        {
            // 显式空数组也是有效配置，不能被当作“未配置”而回退到全部权限。
            var systemPermissions = section.GetChildren().FirstOrDefault(child =>
                string.Equals(child.Key, nameof(settings.SystemAccountPermissions), StringComparison.OrdinalIgnoreCase));
            if (systemPermissions is not null)
                settings.SystemAccountPermissions = systemPermissions.Get<List<WorkbenchPermission>>() ?? [];

            foreach (var user in section.GetSection(nameof(settings.UserPermissions)).GetChildren())
                settings.UserPermissions[user.Key] = user.Get<List<WorkbenchPermission>>() ?? [];
        });
    }
}
