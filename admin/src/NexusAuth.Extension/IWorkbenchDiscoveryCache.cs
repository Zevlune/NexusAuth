namespace NexusAuth.Extension;

/// <summary>Shares provider metadata across OIDC request scopes and coalesces concurrent loads.</summary>
public interface IWorkbenchDiscoveryCache
{
    /// <summary>Returns fresh metadata or loads and caches the document for the supplied authority.</summary>
    Task<DiscoveryDocument> GetAsync(string authority, Func<Task<DiscoveryDocument>> load, CancellationToken ct);
}
