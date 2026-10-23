namespace NexusAuth.Application.Services.WebAuthn;

/// <summary>
/// 经过 FIDO2 attestation 验证后允许写入的 Passkey 公钥数据。
/// </summary>
public sealed record WebAuthnCredentialRegistration(
    Guid UserId,
    byte[] CredentialId,
    byte[] PublicKeyCose,
    uint SignatureCounter,
    Guid AaGuid,
    IReadOnlyCollection<string> Transports,
    bool IsBackupEligible,
    bool IsBackedUp,
    string? DisplayName);
