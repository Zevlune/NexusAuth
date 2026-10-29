namespace NexusAuth.Extension;

internal sealed record InMemoryFlowStateStoreEntry(FlowState State, DateTimeOffset ExpiresAt);
