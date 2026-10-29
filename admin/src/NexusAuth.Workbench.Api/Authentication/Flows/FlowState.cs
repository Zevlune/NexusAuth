namespace NexusAuth.Workbench.Api;

/// <summary>Stores the one-time browser-binding evidence for a pending login flow.</summary>
public sealed record FlowState(string BrowserBinding);
