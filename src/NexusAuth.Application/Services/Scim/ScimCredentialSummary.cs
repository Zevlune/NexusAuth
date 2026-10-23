namespace NexusAuth.Application.Services.Scim;

public sealed record ScimCredentialSummary(Guid Id, string Name, IReadOnlyList<string> Scopes, bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
