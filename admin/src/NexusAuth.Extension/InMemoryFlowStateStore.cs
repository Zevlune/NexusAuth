using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>Bounded, expiring process-local login state. Multi-instance deployments require session affinity.</summary>
public sealed class InMemoryFlowStateStore : IFlowStateStore, IDisposable
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private readonly int capacity;
    private readonly ITimer cleanupTimer;

    /// <summary>Creates a bounded store with an optional clock for deterministic expiry.</summary>
    public InMemoryFlowStateStore(IOptions<WorkbenchFlowStateOptions>? options = null, TimeProvider? timeProvider = null)
    {
        var settings = options?.Value ?? new WorkbenchFlowStateOptions();
        if (settings.Capacity is < 1 or > 100_000 || settings.Lifetime <= TimeSpan.Zero
            || settings.Lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options), "Login state capacity or lifetime is invalid.");
        clock = timeProvider ?? TimeProvider.System;
        lifetime = settings.Lifetime;
        capacity = settings.Capacity;
        cleanupTimer = clock.CreateTimer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public Task AddAsync(string state, FlowState flowState, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            CleanupExpired();
            if (entries.Count >= capacity)
                throw new FlowStateCapacityExceededException();
            if (!entries.TryAdd(state, new Entry(flowState, clock.GetUtcNow() + lifetime)))
                throw new InvalidOperationException("Login state already exists.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FlowState?> TryTakeAsync(string state, string browserBinding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!entries.TryGetValue(state, out var entry))
                return Task.FromResult<FlowState?>(null);
            if (entry.ExpiresAt <= clock.GetUtcNow())
            {
                entries.Remove(state);
                return Task.FromResult<FlowState?>(null);
            }
            if (entry.State.BrowserBinding is null || !FixedTimeEquals(entry.State.BrowserBinding, browserBinding))
                return Task.FromResult<FlowState?>(null);
            entries.Remove(state);
            return Task.FromResult<FlowState?>(entry.State);
        }
    }

    /// <inheritdoc />
    public Task<FlowState?> GetAsync(string state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            CleanupExpired();
            return Task.FromResult(entries.GetValueOrDefault(state)?.State);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string state, CancellationToken ct = default)
    {
        lock (gate) entries.Remove(state);
        return Task.CompletedTask;
    }

    private void Cleanup() { lock (gate) CleanupExpired(); }

    private void CleanupExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var state in entries.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            entries.Remove(state);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <inheritdoc />
    public void Dispose() => cleanupTimer.Dispose();

    private sealed record Entry(FlowState State, DateTimeOffset ExpiresAt);
}
