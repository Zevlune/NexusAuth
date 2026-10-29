using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>Selects the storage provider for temporary OpenID Connect login flows.</summary>
public sealed class OpenIdConnectFlowStateBuilder
{
    /// <summary>Creates a builder over the application's service collection.</summary>
    public OpenIdConnectFlowStateBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Binds flow-state storage selection and settings from configuration.</summary>
    public OpenIdConnectFlowStateBuilder UseConfiguration(
        IConfigurationSection configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Services.AddOptions<OpenIdConnectFlowStateOptions>().Bind(configuration);
        Services.AddOptions<RedisFlowStateOptions>()
            .Bind(configuration.GetSection("Redis"));
        var configurationRequired =
            RedisFlowStateExtensions.RegisterConnection(Services);
        RedisFlowStateExtensions.AddValidation(
            Services,
            configurationRequired);

        Services.RemoveAll<IFlowStateStore>();
        Services.TryAddKeyedSingleton<IFlowStateStore, InMemoryFlowStateStore>(
            OpenIdConnectFlowStateProvider.InMemory);
        Services.TryAddKeyedScoped<IFlowStateStore, RedisFlowStateStore>(
            OpenIdConnectFlowStateProvider.Redis);
        Services.AddScoped<IFlowStateStore>(provider =>
            new ConfiguredFlowStateStore(
                provider,
                provider.GetRequiredService<IOptions<OpenIdConnectFlowStateOptions>>()));
        return this;
    }

    /// <summary>Selects bounded process-local storage shared across request scopes.</summary>
    public OpenIdConnectFlowStateBuilder UseInMemory(
        Action<OpenIdConnectFlowStateOptions>? configure = null)
    {
        if (configure is not null)
            Services.Configure(configure);

        Services.Configure<OpenIdConnectFlowStateOptions>(
            options => options.Provider = OpenIdConnectFlowStateProvider.InMemory);
        return UseFlowStateStore<InMemoryFlowStateStore>(ServiceLifetime.Singleton);
    }

    /// <summary>Selects a custom provider, replacing any previous flow-state registration.</summary>
    public OpenIdConnectFlowStateBuilder UseFlowStateStore<TStore>(
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TStore : class, IFlowStateStore
    {
        if (lifetime is not (ServiceLifetime.Scoped or ServiceLifetime.Singleton))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "Flow-state providers must be scoped or singleton.");
        }

        Services.RemoveAll<IFlowStateStore>();
        Services.Add(
            new ServiceDescriptor(typeof(IFlowStateStore), typeof(TStore), lifetime));
        return this;
    }
}
