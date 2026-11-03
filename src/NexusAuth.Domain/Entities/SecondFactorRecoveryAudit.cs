using Luck.DDD.Domain.Domain.Entities;

namespace NexusAuth.Domain.Entities;

/// <summary>
/// Immutable audit record for an administrator-driven second-factor reset.
/// </summary>
public sealed class SecondFactorRecoveryAudit : EntityWithIdentity<Guid>
{
    public Guid ActorId { get; private set; }

    public Guid UserId { get; private set; }

    public string Reason { get; private set; } = default!;

    public DateTimeOffset OccurredAt { get; private set; }

    private SecondFactorRecoveryAudit(Guid id) : base(id)
    {
    }

    public static SecondFactorRecoveryAudit Create(
        Guid actorId,
        Guid userId,
        string reason,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actorId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var normalizedReason = reason.Trim();
        if (normalizedReason.Length > 500 || normalizedReason.Any(char.IsControl))
            throw new ArgumentException("The recovery reason must be at most 500 characters and contain no control characters.", nameof(reason));

        return new SecondFactorRecoveryAudit(Guid.NewGuid())
        {
            ActorId = actorId,
            UserId = userId,
            Reason = normalizedReason,
            OccurredAt = (occurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        };
    }
}
