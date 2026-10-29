using StackExchange.Redis;

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

    internal void Validate(bool configurationRequired)
    {
        var error = GetValidationError(configurationRequired);
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    internal string? GetValidationError(bool configurationRequired)
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix)
            || KeyPrefix.IndexOfAny(['{', '}']) >= 0)
        {
            return "Auth:FlowState:Redis KeyPrefix must be non-empty and must not contain braces.";
        }

        if (Database < -1)
        {
            return "Auth:FlowState:Redis Database must be -1 or non-negative.";
        }

        return GetConfigurationValidationError(configurationRequired);
    }

    internal string RecordsKey => $"{{{KeyPrefix}}}:records";

    internal string ExpiriesKey => $"{{{KeyPrefix}}}:expiries";

    private string? GetConfigurationValidationError(bool configurationRequired)
    {
        if (string.IsNullOrWhiteSpace(Configuration))
        {
            return configurationRequired
                ? "Auth:FlowState:Redis Configuration is required when no IConnectionMultiplexer is registered."
                : null;
        }

        try
        {
            _ = ConfigurationOptions.Parse(Configuration);
            return null;
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException)
        {
            return "Auth:FlowState:Redis Configuration is not a valid StackExchange.Redis configuration string.";
        }
    }
}
