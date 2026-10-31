using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace NexusAuth.Extension;

/// <summary>合并使用同一 refresh token 的并发刷新，避免旋转令牌被重复消费。</summary>
public interface INexusAuthTokenRefreshCoordinator
{
    /// <summary>刷新令牌；并发调用共享同一次兑换结果。</summary>
    Task<NexusAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken ct);
}

/// <summary>进程内单飞：短暂保留旋转结果，供并发请求复用；实际兑换在独立 scope 中执行。</summary>
public class NexusAuthTokenRefreshCoordinator(IServiceScopeFactory scopes) : INexusAuthTokenRefreshCoordinator
{
    private readonly ConcurrentDictionary<string, Lazy<Task<NexusAuthTokenResult>>> flights = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<NexusAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var key = HashRefreshToken(refreshToken);
        var flight = flights.GetOrAdd(key, _ => new Lazy<Task<NexusAuthTokenResult>>(
            () => RefreshAndRetainAsync(key, refreshToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return flight.Value.WaitAsync(ct);
    }

    /// <summary>执行一次兑换；派生类可在此加入跨实例协调。<paramref name="key"/> 是令牌的 SHA-256 摘要。</summary>
    protected virtual async Task<NexusAuthTokenResult> ExchangeAsync(string key, string refreshToken, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INexusAuthTokenClient>()
            .RefreshTokensAsync(refreshToken, ct);
    }

    private async Task<NexusAuthTokenResult> RefreshAndRetainAsync(string key, string refreshToken)
    {
        try
        {
            // 单飞任务不随某个请求取消，避免首个请求断开导致其他等待者一起失败。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await ExchangeAsync(key, refreshToken, timeout.Token);
        }
        finally { _ = RemoveLaterAsync(key); }
    }

    private async Task RemoveLaterAsync(string key)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        flights.TryRemove(key, out _);
    }

    private static string HashRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
