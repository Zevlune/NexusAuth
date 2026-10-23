namespace NexusAuth.Application.Clients;

public record ClientValidationResult(
    bool IsSuccess,
    string? Error,
    string? ErrorCode,
    bool RedirectUriValidated = false)
{
    public static ClientValidationResult Success()
        => new(true, null, null, true);

    public static ClientValidationResult Failure(string errorCode, string error, bool redirectUriValidated = false)
        => new(false, error, errorCode, redirectUriValidated);
}
