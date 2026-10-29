namespace NexusAuth.Application.Services.DataRetention;

public sealed class AuthenticationDataRetentionOptions
{
    public const string SectionName = "DataRetention";

    public bool Enabled { get; set; } = true;

    public int SweepIntervalMinutes { get; set; } = 5;

    public int ExpiredDataRetentionDays { get; set; } = 7;

    public int LoginAuditRetentionDays { get; set; } = 180;

    public int CleanupBatchSize { get; set; } = 1_000;

    public int MaxBatchesPerTypePerSweep { get; set; } = 20;
}
