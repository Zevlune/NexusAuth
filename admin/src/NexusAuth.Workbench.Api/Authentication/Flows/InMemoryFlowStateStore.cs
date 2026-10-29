using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api;

/// <summary>Bounded, expiring process-local login state shared across request scopes.</summary>
public sealed class InMemoryFlowStateStore : IFlowStateStore, IDisposable
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private readonly int capacity;
    private readonly ITimer cleanupTimer;

    /// <summary>Creates a bounded store whose timer is owned by the DI container.</summary>
    public InMemoryFlowStateStore(
        IOptions<WorkbenchFlowStateOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var settings = options.Value;
        if (settings.Capacity is < 1 or > 100_000
            || settings.Lifetime <= TimeSpan.Zero
            || settings.Lifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Login state capacity or lifetime is invalid.");
        }

        clock = timeProvider;
        lifetime = settings.Lifetime;
        capacity = settings.Capacity;
        cleanupTimer = clock.CreateTimer(
            _ => Cleanup(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public Task AddAsync(string state, FlowState flowState, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);
        ArgumentNullException.ThrowIfNull(flowState);
        ArgumentException.ThrowIfNullOrEmpty(flowState.BrowserBinding);

        lock (gate)
        {
            CleanupExpired();
            if (entries.Count >= capacity)
                throw new FlowStateCapacityExceededException();
            if (!entries.TryAdd(
                    state,
                    new Entry(flowState, clock.GetUtcNow() + lifetime)))
            {
                throw new InvalidOperationException("Login state already exists.");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FlowState?> TryTakeAsync(
        string state,
        string browserBinding,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);
        ArgumentException.ThrowIfNullOrEmpty(browserBinding);

        lock (gate)
        {
            if (!entries.TryGetValue(state, out var entry))
                return Task.FromResult<FlowState?>(null);

            if (entry.ExpiresAt <= clock.GetUtcNow())
            {
                entries.Remove(state);
                return Task.FromResult<FlowState?>(null);
            }

            if (!FixedTimeEquals(entry.State.BrowserBinding, browserBinding))
                return Task.FromResult<FlowState?>(null);

            entries.Remove(state);
            return Task.FromResult<FlowState?>(entry.State);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);

        lock (gate)
            entries.Remove(state);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => cleanupTimer.Dispose();

    private void Cleanup()
    {
        lock (gate)
            CleanupExpired();
    }

    private void CleanupExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var state in entries
                     .Where(item => item.Value.ExpiresAt <= now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            entries.Remove(state);
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length
            && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private sealed record Entry(FlowState State, DateTimeOffset ExpiresAt);
}
