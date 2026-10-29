using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

internal sealed class ConfiguredFlowStateStore : IFlowStateStore
{
    private readonly IFlowStateStore store;

    public ConfiguredFlowStateStore(
        IServiceProvider services,
        IOptions<OpenIdConnectFlowStateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        store = services.GetRequiredKeyedService<IFlowStateStore>(
            options.Value.Provider);
    }

    public Task AddAsync(
        string state,
        FlowState flowState,
        CancellationToken ct = default) =>
        store.AddAsync(state, flowState, ct);

    public Task<FlowState?> TryTakeAsync(
        string state,
        string browserBinding,
        CancellationToken ct = default) =>
        store.TryTakeAsync(state, browserBinding, ct);

    public Task RemoveAsync(string state, CancellationToken ct = default) =>
        store.RemoveAsync(state, ct);
}
