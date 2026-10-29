using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NexusAuth.Workbench.Api;

public interface IWorkbenchTokenRefreshCoordinator
{
    Task<WorkbenchTokenResult> RefreshAsync(string refreshToken, CancellationToken ct);
}

/// <summary>Shares a rotation result briefly across requests, owning a scope for the actual exchange.</summary>
public sealed class WorkbenchTokenRefreshCoordinator(IServiceScopeFactory scopes) : IWorkbenchTokenRefreshCoordinator
{
    private readonly ConcurrentDictionary<string, Lazy<Task<WorkbenchTokenResult>>> flights = new(StringComparer.Ordinal);

    public Task<WorkbenchTokenResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        var flight = flights.GetOrAdd(key, _ => new Lazy<Task<WorkbenchTokenResult>>(
            () => RefreshAndRetainAsync(key, refreshToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return flight.Value.WaitAsync(ct);
    }

    private async Task<WorkbenchTokenResult> RefreshAndRetainAsync(string key, string refreshToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>()
                .RefreshTokensAsync(refreshToken, timeout.Token);
        }
        finally { _ = RemoveLaterAsync(key); }
    }

    private async Task RemoveLaterAsync(string key)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        flights.TryRemove(key, out _);
    }
}
