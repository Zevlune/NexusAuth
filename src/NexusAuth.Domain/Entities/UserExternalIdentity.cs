using Luck.DDD.Domain.Domain.Entities;

namespace NexusAuth.Domain.Entities;

/// <summary>
/// Links a NexusAuth user to an identity issued by an external provider.
/// </summary>
public sealed class UserExternalIdentity : EntityWithIdentity<Guid>
{
    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = default!;
    public string Issuer { get; private set; } = default!;
    /// <summary>
    /// 第三方身份提供方签发的用户唯一标识，在同一 provider 和 issuer 下唯一；微信小程序对应 OpenID（issuer 为 AppID）。
    /// </summary>
    public string Subject { get; private set; } = default!;
    public string? UnionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastAuthenticatedAt { get; private set; }

    private UserExternalIdentity(Guid id) : base(id)
    {
    }

    public static UserExternalIdentity Create(
        Guid userId,
        string provider,
        string issuer,
        string subject,
        string? unionId = null,
        DateTimeOffset? now = null)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user identifier is required.", nameof(userId));

        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return new UserExternalIdentity(Guid.NewGuid())
        {
            UserId = userId,
            Provider = NormalizeRequired(provider, nameof(provider)),
            Issuer = NormalizeRequired(issuer, nameof(issuer)),
            Subject = NormalizeRequired(subject, nameof(subject)),
            UnionId = NormalizeOptional(unionId),
            CreatedAt = timestamp,
            LastAuthenticatedAt = timestamp,
        };
    }

    public void UpdateLastAuthenticatedAt(DateTimeOffset now)
    {
        var timestamp = now.ToUniversalTime();
        if (timestamp < CreatedAt)
            throw new ArgumentOutOfRangeException(nameof(now), "The authentication time cannot precede identity creation.");
        if (timestamp > LastAuthenticatedAt)
            LastAuthenticatedAt = timestamp;
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
