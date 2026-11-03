using Luck.DDD.Domain.Domain.Entities;

namespace NexusAuth.Domain.Entities;

/// <summary>
/// A one-time recovery code owned by one user. Only the SHA-256 hash is
/// persisted; the clear-text value exists only in the generation response.
/// </summary>
public sealed class UserRecoveryCode : EntityWithIdentity<Guid>
{
    public Guid UserId { get; private set; }

    public string CodeHash { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    private UserRecoveryCode(Guid id) : base(id)
    {
    }

    public static UserRecoveryCode Create(
        Guid userId,
        string code,
        DateTimeOffset? now = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var timestamp = now?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        return new UserRecoveryCode(Guid.NewGuid())
        {
            UserId = userId,
            CodeHash = Hash(code),
            CreatedAt = timestamp,
        };
    }

    public static string Hash(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));
    }

    public void MarkUsed(DateTimeOffset now)
    {
        UsedAt ??= now.ToUniversalTime();
    }
}
