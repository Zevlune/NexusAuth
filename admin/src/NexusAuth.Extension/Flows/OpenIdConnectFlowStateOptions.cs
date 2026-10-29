namespace NexusAuth.Extension;

/// <summary>Limits the lifetime and number of pending OpenID Connect login flows.</summary>
public sealed class OpenIdConnectFlowStateOptions
{
    /// <summary>Storage provider; must be InMemory or Redis.</summary>
    public string Provider { get; set; } = "InMemory";

    /// <summary>Maximum lifetime of a pending flow; defaults to ten minutes.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Maximum number of pending flows; defaults to ten thousand.</summary>
    public int Capacity { get; set; } = 10_000;
}

/// <summary>Indicates that the pending login capacity has been reached.</summary>
public sealed class FlowStateCapacityExceededException : InvalidOperationException
{
    /// <summary>Creates a capacity error without including login state or credentials.</summary>
    public FlowStateCapacityExceededException() : base("Too many pending login requests.")
    {
    }
}
