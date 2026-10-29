namespace NexusAuth.Workbench.Api;

/// <summary>Defines storage operations for temporary OIDC login flows.</summary>
public interface IFlowStateStore
{
    /// <summary>Stores a flow associated with the specified state value.</summary>
    Task AddAsync(string state, FlowState flowState, CancellationToken ct = default);

    /// <summary>Atomically consumes an unexpired flow when its browser binding matches.</summary>
    Task<FlowState?> TryTakeAsync(
        string state,
        string browserBinding,
        CancellationToken ct = default);

    /// <summary>Removes the flow associated with the specified state value.</summary>
    Task RemoveAsync(string state, CancellationToken ct = default);
}
