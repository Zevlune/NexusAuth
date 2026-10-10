namespace NexusAuth.Application.Services.Email;

public sealed class EmailCodeRequestException(string message, int retryAfterSeconds, bool hasValidCode = false)
    : InvalidOperationException(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
    public bool HasValidCode { get; } = hasValidCode;
}
