namespace NexusAuth.Extension;

/// <summary>Configures the Redis connection and key namespace used for pending login flows.</summary>
public sealed class RedisFlowStateOptions
{
    /// <summary>The default namespace for Redis-backed flow-state keys.</summary>
    public const string DefaultKeyPrefix = "NexusAuth:oidc:flow";

    /// <summary>Gets or sets the StackExchange.Redis configuration string.</summary>
    public string? Configuration { get; set; }

    /// <summary>Gets or sets the namespace shared by instances using the same flow-state pool.</summary>
    public string KeyPrefix { get; set; } = DefaultKeyPrefix;

    /// <summary>Gets or sets the Redis database; -1 uses the connection default.</summary>
    public int Database { get; set; } = -1;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix)
            || KeyPrefix.IndexOfAny(['{', '}']) >= 0)
        {
            throw new InvalidOperationException(
                "Redis flow-state KeyPrefix must be non-empty and must not contain braces.");
        }

        if (Database < -1)
            throw new InvalidOperationException(
                "Redis flow-state Database must be -1 or non-negative.");
    }

    internal string RecordsKey => $"{{{KeyPrefix}}}:records";

    internal string ExpiriesKey => $"{{{KeyPrefix}}}:expiries";
}
