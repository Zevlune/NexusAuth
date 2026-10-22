namespace NexusAuth.Application.Services.ExternalIdentity;

public interface IWeChatMiniProgramLoginService : IScopedDependency
{
    Task<WeChatMiniProgramLoginResult> AuthenticateAsync(
        string code,
        CancellationToken ct = default);
}
