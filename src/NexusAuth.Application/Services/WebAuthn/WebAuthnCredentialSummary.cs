namespace NexusAuth.Application.Services.WebAuthn;

// Deliberately excludes credential IDs, public keys and authentication counters.
public sealed record WebAuthnCredentialSummary(
    Guid Id, string DisplayName, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
    DateTimeOffset? DisabledAt, bool IsBackupEligible, bool IsBackedUp);
