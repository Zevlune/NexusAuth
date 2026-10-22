namespace NexusAuth.Application.Services.LoginAudits;

public sealed record LoginAuditLogDto(
    Guid Id,
    string Username,
    Guid? UserId,
    string? ClientId,
    bool IsSuccessful,
    string? FailureReason,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset OccurredAt);
