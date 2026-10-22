namespace NexusAuth.Application.Clients;

public record ScopeValidationResult(
    bool IsSuccess,
    string? NormalizedScope,
    string? Error,
    string? ErrorCode)
{
    public static ScopeValidationResult Success(string normalizedScope)
        => new(true, normalizedScope, null, null);

    public static ScopeValidationResult Failure(string errorCode, string error)
        => new(false, null, error, errorCode);
}
