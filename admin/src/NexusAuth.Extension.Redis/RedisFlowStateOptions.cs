namespace NexusAuth.Extension.Redis;

/// <summary>Configures the Redis connection and key namespace used for pending OIDC flows.</summary>
public sealed class RedisFlowStateOptions
{
    /// <summary>The default namespace for the Redis-backed flow-state keys.</summary>
    public const string DefaultKeyPrefix = "NexusAuth:workbench:flow";

    /// <summary>
    /// Gets or sets the StackExchange.Redis configuration string used to create the shared connection.
    /// </summary>
    /// <remarks>
    /// This may be null when the application has already registered an
    /// <see cref="StackExchange.Redis.IConnectionMultiplexer"/>.
    /// </remarks>
    public string? Configuration { get; set; }

    /// <summary>
    /// Gets or sets the namespace shared by every application instance that must use the same flow-state pool.
    /// </summary>
    /// <remarks>
    /// The store adds a fixed Redis Cluster hash tag around this value and creates
    /// <c>{prefix}:records</c> and <c>{prefix}:expiries</c>. Braces are not allowed in the prefix.
    /// </remarks>
    public string KeyPrefix { get; set; } = DefaultKeyPrefix;

    /// <summary>Gets or sets the Redis database. The default of -1 uses the connection's default database.</summary>
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
            throw new InvalidOperationException("Redis flow-state Database must be -1 or non-negative.");
    }

    internal string RecordsKey => $"{{{KeyPrefix}}}:records";

    internal string ExpiriesKey => $"{{{KeyPrefix}}}:expiries";
}
