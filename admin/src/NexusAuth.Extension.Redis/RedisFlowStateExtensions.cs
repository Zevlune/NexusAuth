using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Extension.Redis;

/// <summary>Registers Redis-backed pending OIDC flow-state storage.</summary>
public static class RedisFlowStateExtensions
{
    /// <summary>
    /// Selects Redis flow-state storage and creates a shared multiplexer when the application has not
    /// already registered one.
    /// </summary>
    /// <param name="builder">The NexusAuth builder whose flow-state provider is replaced.</param>
    /// <param name="configure">Configures the Redis connection and key namespace.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// A connection created by this method is a DI-owned singleton and is disposed with its container.
    /// An existing <see cref="IConnectionMultiplexer"/> registration takes precedence and remains owned by
    /// the code that registered it.
    /// </remarks>
    public static NexusAuthBuilder UseRedis(
        this NexusAuthBuilder builder,
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

    /// <summary>
    /// Selects Redis flow-state storage using an <see cref="IConnectionMultiplexer"/> already registered
    /// by the application.
    /// </summary>
    /// <param name="builder">The NexusAuth builder whose flow-state provider is replaced.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// The store uses that shared connection and never disposes it. Configure a custom key namespace by
    /// registering <see cref="RedisFlowStateOptions"/> through the application's options services.
    /// </remarks>
    public static NexusAuthBuilder UseRedis(this NexusAuthBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<RedisFlowStateOptions>();
        return RegisterStore(builder);
    }

    private static NexusAuthBuilder RegisterStore(NexusAuthBuilder builder) =>
        builder.UseFlowStateStore<RedisFlowStateStore>(ServiceLifetime.Scoped);
}
