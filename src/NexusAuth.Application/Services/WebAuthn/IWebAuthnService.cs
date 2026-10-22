namespace NexusAuth.Application.Services.WebAuthn;

/// <summary>
/// 统一编排 WebAuthn challenge 与 credential 的持久化生命周期，Host 层不直接访问仓储。
/// </summary>
public interface IWebAuthnService : IScopedDependency
{
    Task<IReadOnlyList<WebAuthnCredential>> GetEnabledCredentialsAsync(Guid userId, CancellationToken ct = default);

    Task<string> CreateRegistrationChallengeAsync(
        Guid userId,
        string optionsJson,
        string? returnUrl,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    Task<string> CreateAuthenticationChallengeAsync(
        string optionsJson,
        string? returnUrl,
        bool rememberMe,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    Task<WebAuthnChallenge?> ConsumeRegistrationChallengeAsync(
        string flowToken,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<WebAuthnChallenge?> ConsumeAuthenticationChallengeAsync(
        string flowToken,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<WebAuthnCredential?> FindCredentialAsync(byte[] credentialId, CancellationToken ct = default);

    Task RegisterCredentialAsync(WebAuthnCredentialRegistration registration, CancellationToken ct = default);

    Task CompleteAuthenticationAsync(
        WebAuthnChallenge challenge,
        WebAuthnCredential credential,
        uint signatureCounter,
        bool isBackedUp,
        DateTimeOffset authenticatedAt,
        CancellationToken ct = default);
}
