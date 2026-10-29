using NexusAuth.Domain.AggregateRoots.OAuthClients;

namespace NexusAuth.Application.Clients;

public record ClientValidationResult(
    bool IsSuccess,
    string? Error,
    string? ErrorCode,
    bool RedirectUriValidated = false,
    OAuthClient? Client = null)
{
    public static ClientValidationResult Success(OAuthClient? client = null)
        => new(true, null, null, true, client);

    public static ClientValidationResult Failure(string errorCode, string error, bool redirectUriValidated = false)
        => new(false, error, errorCode, redirectUriValidated);
}
