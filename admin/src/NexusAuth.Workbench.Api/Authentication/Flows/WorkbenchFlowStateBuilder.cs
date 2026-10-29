using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusAuth.Workbench.Api;

/// <summary>Selects the storage provider for temporary Workbench login flows.</summary>
public sealed class WorkbenchFlowStateBuilder
{
    /// <summary>Creates a builder over the application's service collection.</summary>
    public WorkbenchFlowStateBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Selects bounded process-local storage shared across request scopes.</summary>
    public WorkbenchFlowStateBuilder UseInMemory(
        Action<WorkbenchFlowStateOptions>? configure = null)
    {
        if (configure is not null)
            Services.Configure(configure);

        return UseFlowStateStore<InMemoryFlowStateStore>(ServiceLifetime.Singleton);
    }

    /// <summary>Selects a custom provider, replacing any previous flow-state registration.</summary>
    public WorkbenchFlowStateBuilder UseFlowStateStore<TStore>(
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
