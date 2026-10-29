namespace NexusAuth.Extension;

/// <summary>Indicates that the pending login capacity has been reached.</summary>
public sealed class FlowStateCapacityExceededException : InvalidOperationException
{
    /// <summary>Creates a capacity error without including login state or credentials.</summary>
    public FlowStateCapacityExceededException() : base("Too many pending login requests.")
    {
    }
}
