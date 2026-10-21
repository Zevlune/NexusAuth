namespace NexusAuth.Application.Services.ExternalIdentity;

public sealed class WeChatMiniProgramLoginException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
