namespace NexusAuth.Extension;

/// <summary>Shares discovery for five minutes and coalesces concurrent requests across service scopes.</summary>
public sealed class WorkbenchDiscoveryCache : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (DiscoveryDocument Document, DateTimeOffset ExpiresAt)> entries = new(StringComparer.Ordinal);

    /// <summary>Returns fresh metadata or loads it once for concurrent callers.</summary>
    public async Task<DiscoveryDocument> GetAsync(string authority, Func<Task<DiscoveryDocument>> load, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (entries.TryGetValue(authority, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
                return entry.Document;
            var document = await load();
            entries[authority] = (document, DateTimeOffset.UtcNow.AddMinutes(5));
            return document;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();
}
