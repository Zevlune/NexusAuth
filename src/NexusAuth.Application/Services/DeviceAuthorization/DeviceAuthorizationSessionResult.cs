namespace NexusAuth.Application.Services.DeviceAuthorization;

public record DeviceAuthorizationSessionResult(
    bool IsSuccess,
    string? Error,
    string? UserCode,
    string? ClientId,
    string? Scope,
    bool IsPending,
    bool IsApproved,
    bool IsDenied)
{
    public static DeviceAuthorizationSessionResult Success(string userCode, string clientId, string scope, bool isPending, bool isApproved, bool isDenied)
        => new(true, null, userCode, clientId, scope, isPending, isApproved, isDenied);

    public static DeviceAuthorizationSessionResult Failure(string error)
        => new(false, error, null, null, null, false, false, false);
}
