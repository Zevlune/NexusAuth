namespace NexusAuth.Application.Services.LoginAudits;

public sealed record LoginAuditRecord(
    string? Username,
    Guid? UserId,
    string? ClientId,
    bool IsSuccessful,
    string? FailureReason,
    string? IpAddress,
    string? UserAgent);
