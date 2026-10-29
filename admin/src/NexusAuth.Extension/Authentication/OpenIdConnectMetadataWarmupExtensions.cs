using Microsoft.Extensions.DependencyInjection;

namespace NexusAuth.Extension;

/// <summary>提前加载标准 OIDC 中间件的元数据，不维护独立缓存。</summary>
public static class OpenIdConnectMetadataWarmupExtensions
{
    /// <summary>
    /// 在后台预热所有已注册 OIDC 认证方案的原生 ConfigurationManager。
    /// 失败的方案每 30 秒重试，成功后停止预热；后续缓存和密钥刷新仍由框架负责。
    /// </summary>
    public static IServiceCollection AddOpenIdConnectMetadataWarmup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<MetadataWarmupService>();
        return services;
    }
}
