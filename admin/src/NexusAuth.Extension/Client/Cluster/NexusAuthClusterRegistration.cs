using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Extension;

/// <summary>注册多实例部署所需的共享连接、密钥环与刷新协调。</summary>
internal static class NexusAuthClusterRegistration
{
    /// <summary>
    /// 在流程状态注册之前调用：配置了 Auth:Redis 时注册共享连接，流程状态的 Redis 存储会复用它。
    /// 返回注册前应用是否已自行提供 IConnectionMultiplexer。
    /// </summary>
    public static bool AddConnection(IServiceCollection services, IConfigurationSection section)
    {
        var external = services.Any(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        var configuration = section.GetSection("Redis")["Configuration"];
        if (!external && !string.IsNullOrWhiteSpace(configuration))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(configuration);
                // Redis 暂时不可用时不阻塞启动，命令在恢复后自动重连。
                options.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(options);
            });
        }

        return external || !string.IsNullOrWhiteSpace(configuration);
    }

    public static void AddDataProtection(IServiceCollection services, IConfigurationSection section)
    {
        // Data Protection 需在注册期确定应用名和存储位置，因此直接读取配置节。
        var settings = section.GetSection("DataProtection").Get<NexusAuthDataProtectionOptions>()
            ?? new NexusAuthDataProtectionOptions();
        if (settings.Provider == NexusAuthProviders.None)
            return;

        var builder = services.AddDataProtection();
        if (!string.IsNullOrWhiteSpace(settings.ApplicationName))
            builder.SetApplicationName(settings.ApplicationName.Trim());

        switch (settings.Provider)
        {
            case NexusAuthProviders.FileSystem when !string.IsNullOrWhiteSpace(settings.KeyPath):
                builder.PersistKeysToFileSystem(new DirectoryInfo(settings.KeyPath));
                break;
            case NexusAuthProviders.Redis:
                services.AddOptions<KeyManagementOptions>()
                    .Configure<IServiceProvider, IOptions<NexusAuthClientOptions>>((options, provider, client) =>
                    {
                        var name = settings.ApplicationName?.Trim();
                        options.XmlRepository = new RedisXmlRepository(
                            () => provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase(),
                            $"{client.Value.Redis.KeyPrefix}:{name}:data-protection-keys");
                    });
                break;
        }
    }

    public static void AddTokenRefresh(IServiceCollection services)
    {
        services.TryAddSingleton<NexusAuthTokenRefreshCoordinator>();
        services.TryAddSingleton<RedisTokenRefreshCoordinator>();
        // 按配置选择实现；未选择 Redis 时不会解析 Redis 连接。
        services.TryAddSingleton<INexusAuthTokenRefreshCoordinator>(provider =>
            provider.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value.TokenRefresh.Provider
                == NexusAuthProviders.Redis
                ? provider.GetRequiredService<RedisTokenRefreshCoordinator>()
                : provider.GetRequiredService<NexusAuthTokenRefreshCoordinator>());
    }
}
