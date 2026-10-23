using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Application.Services.ExternalIdentity;

public sealed record WeChatMiniProgramLoginResult(
    User User,
    DateTimeOffset AuthenticatedAt,
    string Provider,
    string Issuer,
    string Subject,
    string? UnionId);
