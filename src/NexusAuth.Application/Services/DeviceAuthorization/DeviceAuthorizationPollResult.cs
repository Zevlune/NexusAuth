namespace NexusAuth.Application.Services.DeviceAuthorization;

public record DeviceAuthorizationPollResult(
    bool IsSuccess,
    string? Error,
    string? ErrorCode,
    Guid UserId,
    string? Scope,
    string? ClientId,
    int Interval)
{
    public static DeviceAuthorizationPollResult Success(Guid userId, string clientId, string scope)
        => new(true, null, null, userId, scope, clientId, 0);

    public static DeviceAuthorizationPollResult Failure(string errorCode, string error, int interval = 0)
        => new(false, error, errorCode, Guid.Empty, null, null, interval);

    public static DeviceAuthorizationPollResult Pending(int interval = 5)
        => new(false, "Authorization is pending.", "authorization_pending", Guid.Empty, null, null, interval);
}
