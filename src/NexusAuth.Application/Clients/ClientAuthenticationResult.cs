namespace NexusAuth.Application.Clients;

public record ClientAuthenticationResult(
    bool IsSuccess,
    Domain.AggregateRoots.OAuthClients.OAuthClient? Client,
    string? Error,
    string? ErrorCode)
{
    public static ClientAuthenticationResult Success(Domain.AggregateRoots.OAuthClients.OAuthClient client)
        => new(true, client, null, null);

    public static ClientAuthenticationResult Failure(string errorCode, string error)
        => new(false, null, error, errorCode);
}
