namespace NexusAuth.Application.Services.Security;

public record PolicyCheckResult(bool IsSuccess, string? Error)
{
    public static PolicyCheckResult Success() => new(true, null);

    public static PolicyCheckResult Failure(string error) => new(false, error);
}
