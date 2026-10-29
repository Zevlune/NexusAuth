using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Extension;

/// <summary>Registers Redis-backed pending login-flow storage.</summary>
public static class RedisFlowStateExtensions
{
    private static readonly Func<IServiceProvider, object> ConnectionFactory =
        CreateConnection;

    /// <summary>Selects Redis storage and creates a shared multiplexer when none is registered.</summary>
    public static OpenIdConnectFlowStateBuilder UseRedis(
        this OpenIdConnectFlowStateBuilder builder,
        Action<RedisFlowStateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<RedisFlowStateOptions>().Configure(configure);
        var configurationRequired = RegisterConnection(builder.Services);
        AddValidation(builder.Services, configurationRequired);
        builder.Services.Configure<OpenIdConnectFlowStateOptions>(
            options => options.Provider = OpenIdConnectFlowStateProvider.Redis);

        return RegisterStore(builder);
    }

    /// <summary>Selects Redis storage using an already registered multiplexer.</summary>
    public static OpenIdConnectFlowStateBuilder UseRedis(
        this OpenIdConnectFlowStateBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configurationRequired = RegisterConnection(builder.Services);
        AddValidation(builder.Services, configurationRequired);
        builder.Services.Configure<OpenIdConnectFlowStateOptions>(
            options => options.Provider = OpenIdConnectFlowStateProvider.Redis);
        return RegisterStore(builder);
    }

    internal static bool RegisterConnection(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RedisFlowStateOptions>();
        var hasExternalConnection = services.Any(
            descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)
                && !ReferenceEquals(
                    descriptor.ImplementationFactory,
                    ConnectionFactory));
        services.TryAdd(
            ServiceDescriptor.Singleton(
                typeof(IConnectionMultiplexer),
                ConnectionFactory));
        return !hasExternalConnection;
    }

    internal static void AddValidation(
        IServiceCollection services,
        bool configurationRequired)
    {
        services.AddSingleton<IValidateOptions<RedisFlowStateOptions>>(
            provider => new RedisFlowStateOptionsValidator(
                provider.GetRequiredService<
                    IOptions<OpenIdConnectFlowStateOptions>>(),
                configurationRequired));
    }

    private static OpenIdConnectFlowStateBuilder RegisterStore(
        OpenIdConnectFlowStateBuilder builder) =>
        builder.UseFlowStateStore<RedisFlowStateStore>(ServiceLifetime.Scoped);

    private static object CreateConnection(IServiceProvider provider)
    {
        var settings = provider
            .GetRequiredService<IOptions<RedisFlowStateOptions>>()
            .Value;
        settings.Validate(configurationRequired: true);
        return ConnectionMultiplexer.Connect(settings.Configuration!);
    }
}
