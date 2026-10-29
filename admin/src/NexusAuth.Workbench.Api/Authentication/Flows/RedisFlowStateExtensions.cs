using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Workbench.Api;

/// <summary>Registers Redis-backed pending login-flow storage.</summary>
public static class RedisFlowStateExtensions
{
    /// <summary>Selects Redis storage and creates a shared multiplexer when none is registered.</summary>
    public static WorkbenchFlowStateBuilder UseRedis(
        this WorkbenchFlowStateBuilder builder,
        Action<RedisFlowStateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<RedisFlowStateOptions>().Configure(configure);
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(provider =>
        {
            var settings = provider
                .GetRequiredService<IOptions<RedisFlowStateOptions>>()
                .Value;
            settings.Validate();
            if (string.IsNullOrWhiteSpace(settings.Configuration))
            {
                throw new InvalidOperationException(
                    "Redis flow-state Configuration is required when no IConnectionMultiplexer is registered.");
            }

            return ConnectionMultiplexer.Connect(settings.Configuration);
        });

        return RegisterStore(builder);
    }

    /// <summary>Selects Redis storage using an already registered multiplexer.</summary>
    public static WorkbenchFlowStateBuilder UseRedis(
        this WorkbenchFlowStateBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<RedisFlowStateOptions>();
        return RegisterStore(builder);
    }

    private static WorkbenchFlowStateBuilder RegisterStore(
        WorkbenchFlowStateBuilder builder) =>
        builder.UseFlowStateStore<RedisFlowStateStore>(ServiceLifetime.Scoped);
}
