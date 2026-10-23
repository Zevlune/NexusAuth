namespace NexusAuth.Application.Users;

public sealed class TotpOptions
{
    public const string SectionName = "Totp";

    public string Issuer { get; set; } = "NexusAuth";

    public int EnrollmentLifetimeMinutes { get; set; } = 10;
}
