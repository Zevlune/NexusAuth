using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Application.Services.ExternalIdentity;

public interface IWeChatMiniProgramLoginService : IScopedDependency
{
    Task<WeChatMiniProgramLoginResult> AuthenticateAsync(
        string code,
        CancellationToken ct = default);
}

public sealed record WeChatMiniProgramLoginResult(
    User User,
    DateTimeOffset AuthenticatedAt,
    string Provider,
    string Issuer,
    string Subject,
    string? UnionId);
