using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NexusAuth.Extension.Redis;

/// <summary>
/// Stores pending OIDC flow state in Redis with a cross-instance shared capacity and atomic one-time
/// consumption.
/// </summary>
/// <remarks>
/// Every instance that uses the same Redis database and <see cref="RedisFlowStateOptions.KeyPrefix"/>
/// shares one capacity pool and sees the same pending flows. Use a distinct prefix for each isolated
/// application or tenant. A configured multiplexer is borrowed from DI and is never disposed here.
/// </remarks>
public sealed class RedisFlowStateStore : IFlowStateStore
{
    private const int Success = 0;
    private const int DuplicateState = 1;
    private const int CapacityExceeded = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const string LuaPrelude = """
        local function current_time_ms()
            local server_time = redis.call('TIME')
            return tonumber(server_time[1]) * 1000
                + math.floor(tonumber(server_time[2]) / 1000)
        end

        local function remove_expired(records_key, expiries_key, now_ms)
            local expired = redis.call('ZRANGEBYSCORE', expiries_key, '-inf', now_ms)
            if #expired > 0 then
                -- Keep each HDEL below the Lua unpack stack limit when all 100,000 slots expire.
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

            -- Both keys always share a TTL. If one key expires first, discard the unindexed residue.
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

    private const string GetScript = LuaPrelude + """

        local now_ms = current_time_ms()
        remove_expired(KEYS[1], KEYS[2], now_ms)

        local value = redis.call('HGET', KEYS[1], ARGV[1])
        if not value then
            return nil
        end

        local indexed_expiry = redis.call('ZSCORE', KEYS[2], ARGV[1])
        if not indexed_expiry or tonumber(indexed_expiry) <= now_ms then
            redis.call('HDEL', KEYS[1], ARGV[1])
            redis.call('ZREM', KEYS[2], ARGV[1])
            refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
            return nil
        end

        refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
        return value
        """;

    private const string TryTakeScript = LuaPrelude + """

        local now_ms = current_time_ms()
        remove_expired(KEYS[1], KEYS[2], now_ms)

        local value = redis.call('HGET', KEYS[1], ARGV[1])
        if not value then
            return nil
        end

        local indexed_expiry = redis.call('ZSCORE', KEYS[2], ARGV[1])
        if not indexed_expiry or tonumber(indexed_expiry) <= now_ms then
            redis.call('HDEL', KEYS[1], ARGV[1])
            redis.call('ZREM', KEYS[2], ARGV[1])
            refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
            return nil
        end

        local record = cjson.decode(value)
        if not record['bindingHash'] or record['bindingHash'] ~= ARGV[2] then
            return nil
        end

        redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        refresh_key_ttl(KEYS[1], KEYS[2], now_ms)
        return value
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
    /// <param name="connection">The shared multiplexer. The store does not own or dispose it.</param>
    /// <param name="redisOptions">The Redis key namespace and database settings.</param>
    /// <param name="flowOptions">The shared lifetime and capacity limits.</param>
    public RedisFlowStateStore(
        IConnectionMultiplexer connection,
        IOptions<RedisFlowStateOptions> redisOptions,
        IOptions<WorkbenchFlowStateOptions> flowOptions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(redisOptions);
        ArgumentNullException.ThrowIfNull(flowOptions);

        var redisSettings = redisOptions.Value;
        var flowSettings = flowOptions.Value;
        redisSettings.Validate();

        if (flowSettings.Capacity is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(flowOptions), "Flow-state capacity is invalid.");
        if (flowSettings.Lifetime <= TimeSpan.Zero || flowSettings.Lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(flowOptions), "Flow-state lifetime is invalid.");

        database = connection.GetDatabase(redisSettings.Database);
        recordsKey = redisSettings.RecordsKey;
        expiriesKey = redisSettings.ExpiriesKey;
        capacity = flowSettings.Capacity;
        lifetimeMilliseconds = Math.Max(1, (long)Math.Ceiling(flowSettings.Lifetime.TotalMilliseconds));
    }

    /// <inheritdoc />
    public async Task AddAsync(string state, FlowState flowState, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);
        ArgumentNullException.ThrowIfNull(flowState);

        var record = new StoredFlowState(
            new FlowStatePayload(
                flowState.CodeVerifier,
                flowState.Nonce,
                flowState.BrowserBinding),
            HashBinding(flowState.BrowserBinding));
        var json = JsonSerializer.Serialize(record, SerializerOptions);
        var result = await EvaluateAsync(
            AddScript,
            [state, json, lifetimeMilliseconds, capacity],
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
                throw new InvalidOperationException("Redis flow-state add script returned an unknown result.");
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
        return result.IsNull ? null : DeserializeFlowState(result.ToString());
    }

    /// <inheritdoc />
    public async Task<FlowState?> GetAsync(string state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(state);

        var result = await EvaluateAsync(GetScript, [state], ct).ConfigureAwait(false);
        if (result.IsNull)
            return null;

        try
        {
            return DeserializeFlowState(result.ToString());
        }
        catch (JsonException)
        {
            await RemoveAsync(state, ct).ConfigureAwait(false);
            return null;
        }
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

    private static FlowState DeserializeFlowState(string? json)
    {
        var record = JsonSerializer.Deserialize<StoredFlowState>(json ?? string.Empty, SerializerOptions)
            ?? throw new JsonException("The stored Redis flow state is empty.");
        return new FlowState(
            record.Payload.CodeVerifier,
            record.Payload.Nonce,
            record.Payload.BrowserBinding);
    }

    private static string? HashBinding(string? browserBinding) =>
        browserBinding is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(browserBinding)));

    private sealed record StoredFlowState(FlowStatePayload Payload, string? BindingHash);

    private sealed record FlowStatePayload(
        string CodeVerifier,
        string Nonce,
        string? BrowserBinding);
}
