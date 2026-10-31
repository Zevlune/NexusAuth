using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Extension;

/// <summary>
/// 跨实例协调刷新：同一 refresh token 只由持有 Redis 锁的实例兑换，结果经 Data Protection 加密后短暂共享，
/// 其他实例读取同一结果，避免旋转令牌在集群中被重复消费而触发令牌族撤销。
/// </summary>
public sealed class RedisTokenRefreshCoordinator : NexusAuthTokenRefreshCoordinator
{
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private static readonly TimeSpan LockLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConnectionMultiplexer connection;
    private readonly IDataProtector protector;
    private readonly NexusAuthClientOptions settings;

    /// <summary>创建协调器；Redis 连接与 Data Protection 密钥环须在所有实例间共享。</summary>
    public RedisTokenRefreshCoordinator(
        IServiceScopeFactory scopes,
        IConnectionMultiplexer connection,
        IDataProtectionProvider dataProtection,
        IOptions<NexusAuthClientOptions> options) : base(scopes)
    {
        this.connection = connection;
        protector = dataProtection.CreateProtector("NexusAuth.Extension.TokenRefresh");
        settings = options.Value;
    }

    /// <inheritdoc />
    protected override async Task<NexusAuthTokenResult> ExchangeAsync(
        string key, string refreshToken, CancellationToken ct)
    {
        var database = connection.GetDatabase();
        // 键只含令牌摘要；同一摘要的锁和结果放在同一 hash slot。
        var prefix = $"{{{settings.Redis.KeyPrefix}:refresh:{key}}}";
        RedisKey lockKey = prefix + ":lock";
        RedisKey resultKey = prefix + ":result";

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await ReadResultAsync(database, resultKey) is { } shared)
                return shared;

            var owner = Guid.NewGuid().ToString("N");
            if (await database.StringSetAsync(lockKey, owner, LockLifetime, When.NotExists))
            {
                try
                {
                    // 拿锁后再读一次：上一个持锁者可能刚写入结果并释放锁。
                    if (await ReadResultAsync(database, resultKey) is { } late)
                        return late;
                    var result = await base.ExchangeAsync(key, refreshToken, ct);
                    var payload = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(result));
                    await database.StringSetAsync(resultKey, payload, settings.TokenRefresh.ResultLifetime);
                    return result;
                }
                finally
                {
                    // 只释放自己的锁；兑换失败时不写结果，等待者会重新竞争。
                    await database.ScriptEvaluateAsync(ReleaseScript, [lockKey], [owner]);
                }
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task<NexusAuthTokenResult?> ReadResultAsync(IDatabase database, RedisKey resultKey)
    {
        var payload = (byte[]?)await database.StringGetAsync(resultKey);
        if (payload is null)
            return null;
        // 解密失败说明实例间未共享密钥环，应作为配置错误暴露，而不是再次兑换已旋转的令牌。
        return JsonSerializer.Deserialize<NexusAuthTokenResult>(protector.Unprotect(payload))
            ?? throw new InvalidOperationException("The shared refresh result is invalid.");
    }
}
