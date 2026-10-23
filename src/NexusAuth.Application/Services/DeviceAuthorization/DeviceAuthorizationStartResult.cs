namespace NexusAuth.Application.Services.DeviceAuthorization;

public record DeviceAuthorizationStartResult(
    bool IsSuccess,
    string? Error,
    string? ErrorCode,
    string? DeviceCode,
    string? UserCode,
    int ExpiresIn,
    int Interval,
    string? VerificationUri,
    string? VerificationUriComplete)
{
    public static DeviceAuthorizationStartResult Success(string deviceCode, string userCode, int expiresIn, int interval, string verificationUri, string verificationUriComplete)
        => new(true, null, null, deviceCode, userCode, expiresIn, interval, verificationUri, verificationUriComplete);

    public static DeviceAuthorizationStartResult Failure(string errorCode, string error)
        => new(false, error, errorCode, null, null, 0, 0, null, null);
}
