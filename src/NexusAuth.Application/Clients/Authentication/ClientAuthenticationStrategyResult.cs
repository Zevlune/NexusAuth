namespace NexusAuth.Application.Clients.Authentication;

public record ClientAuthenticationStrategyResult(
    bool IsSuccess,
    string AuditReason,
    string? Error = null)
{
    public static ClientAuthenticationStrategyResult Success(string auditReason)
        => new(true, auditReason);

    public static ClientAuthenticationStrategyResult Failure(string auditReason, string error)
        => new(false, auditReason, error);
}
