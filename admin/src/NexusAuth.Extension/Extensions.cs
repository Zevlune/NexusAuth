using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>
/// 中文：提供将 NexusAuth OIDC Workbench 功能注册到依赖注入容器的扩展方法。
/// English: Provides extension methods for registering NexusAuth OIDC Workbench services with dependency injection.
/// </summary>
public static class OidcWorkbenchExtensions
{
    /// <summary>
    /// 中文：注册 NexusAuth OIDC 服务，由接入方在返回的构建器上选择流程状态存储。
    /// English: Registers OIDC services; the caller selects flow-state storage on the returned builder.
    /// </summary>
    /// <param name="services">中文：要添加服务的依赖注入集合。English: The dependency injection collection to which services are added.</param>
    /// <param name="configure">中文：用于配置 Workbench OIDC 客户端的委托。English: A delegate that configures the Workbench OIDC client.</param>
    /// <returns>A builder for selecting an in-memory, Redis, or custom flow-state provider.</returns>
    /// <exception cref="ArgumentNullException">中文：<paramref name="services"/> 或 <paramref name="configure"/> 为 <see langword="null"/>。English: <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static NexusAuthBuilder AddNexusAuth(
        this IServiceCollection services,
        Action<WorkbenchAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<WorkbenchAuthOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WorkbenchAuthOptions>, WorkbenchAuthOptionsValidator>());
        services.AddOptions<WorkbenchFlowStateOptions>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IWorkbenchDiscoveryCache, WorkbenchDiscoveryCache>();

        services.AddHttpClient(OidcWorkbenchService.HttpClientName);
        services.TryAddScoped<IOidcWorkbenchService, OidcWorkbenchService>();

        return new NexusAuthBuilder(services);
    }
}
