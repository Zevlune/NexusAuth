using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Extension;

/// <summary>
/// Stores browser-bound OIDC flow evidence in Redis with shared capacity and atomic one-time
/// consumption.
/// </summary>
public sealed class RedisFlowStateStore : IFlowStateStore
{
    private const int Success = 0;
    private const int DuplicateState = 1;
    private const int CapacityExceeded = 2;

    private const string LuaPrelude = """
        local function current_time_ms()
            local server_time = redis.call('TIME')
            return tonumber(server_time[1]) * 1000
                + math.floor(tonumber(server_time[2]) / 1000)
        end

        local function remove_expired(records_key, expiries_key, now_ms)
            local expired = redis.call('ZRANGEBYSCORE', expiries_key, '-inf', now_ms)
            if #expired > 0 then
                for first = 1, #expired, 500 do
                    local batch = {}
                    local last = math.min(first + 499, #expired)
                    for index = first, last do
                        batch[#batch + 1] = expired[index]
                    end
                    redis.call('HDEL', records_key, unpack(batch))
                end
                redis.call('ZREMRANGEBYSCORE', expiries_key, '-inf', now_ms)
            end

            if redis.call('ZCARD', expiries_key) == 0 then
                redis.call('DEL', records_key, expiries_key)
            end
        end

        local function refresh_key_ttl(records_key, expiries_key, now_ms)
            local latest = redis.call('ZRANGE', expiries_key, -1, -1, 'WITHSCORES')
            if #latest == 0 then
                redis.call('DEL', records_key, expiries_key)
                return
            end

            local remaining_ms = tonumber(latest[2]) - now_ms
            if remaining_ms < 1 then
                remaining_ms = 1
            end
            redis.call('PEXPIRE', records_key, remaining_ms)
            redis.call('PEXPIRE', expiries_key, remaining_ms)
        end
        """;

    private const string AddScript = LuaPrelude + """

        local now_ms = current_time_ms()
        remove_expired(KEYS[1], KEYS[2], now_ms)

        if redis.call('HEXISTS', KEYS[1], ARGV[1]) == 1 then
            local indexed_expiry = redis.call('ZSCORE', KEYS[2], ARGV[1])
            if indexed_expiry and tonumber(indexed_expiry) > now_ms then
                refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
                return 1
            end

            redis.call('HDEL', KEYS[1], ARGV[1])
            redis.call('ZREM', KEYS[2], ARGV[1])
        end

        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[4]) then
            refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
            return 2
        end

        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('ZADD', KEYS[2], now_ms + tonumber(ARGV[3]), ARGV[1])
        refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
        return 0
        """;

    private const string TryTakeScript = LuaPrelude + """

        local now_ms = current_time_ms()
        remove_expired(KEYS[1], KEYS[2], now_ms)

        local binding_hash = redis.call('HGET', KEYS[1], ARGV[1])
        if not binding_hash or binding_hash ~= ARGV[2] then
            return 0
        end

        local indexed_expiry = redis.call('ZSCORE', KEYS[2], ARGV[1])
        if not indexed_expiry or tonumber(indexed_expiry) <= now_ms then
            redis.call('HDEL', KEYS[1], ARGV[1])
            redis.call('ZREM', KEYS[2], ARGV[1])
            refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
            return 0
        end

        redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
        return 1
        """;

    private const string RemoveScript = LuaPrelude + """

        local now_ms = current_time_ms()
        remove_expired(KEYS[1], KEYS[2], now_ms)

        local removed = redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
        return removed
        """;

    private readonly IDatabase database;
    private readonly RedisKey recordsKey;
    private readonly RedisKey expiriesKey;
    private readonly long lifetimeMilliseconds;
    private readonly int capacity;

    /// <summary>Creates a scoped store over the registered Redis connection.</summary>
    public RedisFlowStateStore(
        IConnectionMultiplexer connection,
        IOptions<RedisFlowStateOptions> redisOptions,
        IOptions<OpenIdConnectFlowStateOptions> flowOptions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(redisOptions);
        ArgumentNullException.ThrowIfNull(flowOptions);

        var redisSettings = redisOptions.Value;
        var flowSettings = flowOptions.Value;
        redisSettings.Validate();

        if (flowSettings.Capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(flowOptions),
                "Flow-state capacity is invalid.");
        }

        if (flowSettings.Lifetime <= TimeSpan.Zero
            || flowSettings.Lifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(flowOptions),
                "Flow-state lifetime is invalid.");
        }

        database = connection.GetDatabase(redisSettings.Database);
        recordsKey = redisSettings.RecordsKey;
        expiriesKey = redisSettings.ExpiriesKey;
        capacity = flowSettings.Capacity;
        lifetimeMilliseconds = Math.Max(
            1,
            (long)Math.Ceiling(flowSettings.Lifetime.TotalMilliseconds));
    }

    /// <inheritdoc />
    public async Task AddAsync(
        string state,
        FlowState flowState,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);
        ArgumentNullException.ThrowIfNull(flowState);
        ArgumentException.ThrowIfNullOrEmpty(flowState.BrowserBinding);

        var result = await EvaluateAsync(
            AddScript,
            [state, HashBinding(flowState.BrowserBinding), lifetimeMilliseconds, capacity],
            ct).ConfigureAwait(false);

        switch ((long)result)
        {
            case Success:
                return;
            case DuplicateState:
                throw new InvalidOperationException("Login state already exists.");
            case CapacityExceeded:
                throw new FlowStateCapacityExceededException();
            default:
                throw new InvalidOperationException(
                    "Redis flow-state add script returned an unknown result.");
        }
    }

    /// <inheritdoc />
    public async Task<FlowState?> TryTakeAsync(
        string state,
        string browserBinding,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);
        ArgumentException.ThrowIfNullOrEmpty(browserBinding);

        var result = await EvaluateAsync(
            TryTakeScript,
            [state, HashBinding(browserBinding)],
            ct).ConfigureAwait(false);
        return (long)result == 1 ? new FlowState(browserBinding) : null;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);

        await EvaluateAsync(RemoveScript, [state], ct).ConfigureAwait(false);
    }

    private Task<RedisResult> EvaluateAsync(
        string script,
        RedisValue[] values,
        CancellationToken ct) =>
        database
            .ScriptEvaluateAsync(script, [recordsKey, expiriesKey], values)
            .WaitAsync(ct);

    private static string HashBinding(string browserBinding) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(browserBinding)));
}
