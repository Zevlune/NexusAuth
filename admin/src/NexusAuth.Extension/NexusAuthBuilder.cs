using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Extension;

/// <summary>Selects the flow-state provider used by NexusAuth OIDC services.</summary>
public sealed class NexusAuthBuilder
{
    /// <summary>The application's service collection, available to optional storage providers.</summary>
    public IServiceCollection Services { get; }

    internal NexusAuthBuilder(IServiceCollection services) => Services = services;

    /// <summary>Selects bounded process-local storage shared across request scopes.</summary>
    /// <remarks>Multiple application instances need session affinity or a shared provider such as Redis.</remarks>
    public NexusAuthBuilder UseInMemory(Action<WorkbenchFlowStateOptions>? configure = null)
    {
        if (configure is not null) Services.Configure(configure);
        return UseFlowStateStore<InMemoryFlowStateStore>(ServiceLifetime.Singleton);
    }

    /// <summary>Selects a custom provider, replacing any previously selected flow-state registration.</summary>
    /// <remarks>
    /// Scoped is appropriate for providers backed by shared external storage. A provider storing state
    /// in its own fields must be a singleton so login and callback requests can access the same state.
    /// Dependencies and disposable resources are owned by the DI container.
    /// </remarks>
    public NexusAuthBuilder UseFlowStateStore<TStore>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TStore : class, IFlowStateStore
    {
        if (lifetime is not (ServiceLifetime.Scoped or ServiceLifetime.Singleton))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Flow-state providers must be scoped or singleton.");
        Services.RemoveAll<IFlowStateStore>();
        Services.Add(new ServiceDescriptor(typeof(IFlowStateStore), typeof(TStore), lifetime));
        return this;
    }
}
