namespace NexusAuth.Application.Services.WebAuthn;

public interface IPasskeyManagementService : IScopedDependency
{
    Task<IReadOnlyList<WebAuthnCredentialSummary>> GetAsync(Guid userId, CancellationToken ct = default);
    Task<bool> RenameAsync(Guid userId, Guid id, string displayName, CancellationToken ct = default);
    Task<bool> RevokeAsync(Guid userId, Guid id, Guid actorId, string reason, CancellationToken ct = default);
}

// Deliberately excludes credential IDs, public keys and authentication counters.
public sealed record WebAuthnCredentialSummary(
    Guid Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
    DateTimeOffset? DisabledAt, bool IsBackupEligible, bool IsBackedUp);
