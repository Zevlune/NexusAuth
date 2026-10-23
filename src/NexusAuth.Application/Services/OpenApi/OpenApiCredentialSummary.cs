namespace NexusAuth.Application.Services.OpenApi;

public sealed record OpenApiCredentialSummary(Guid Id, string Name, string TargetType, IReadOnlyList<string> Scopes, bool IsActive, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
