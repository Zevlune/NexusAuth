namespace NexusAuth.Application.Clients;

public record ClientAuthenticationParseResult(
    bool IsSuccess,
    ClientAuthenticationInput? Authentication,
    string? Error,
    string? ErrorCode)
{
    public static ClientAuthenticationParseResult Success(ClientAuthenticationInput authentication)
        => new(true, authentication, null, null);

    public static ClientAuthenticationParseResult Failure(string error = "Invalid client authentication.")
        => new(false, null, error, "invalid_client");
}
