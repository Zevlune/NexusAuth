namespace NexusAuth.Application.Users;

/// <summary>
/// TOTP enrollment and verification operations used by the login flow.
/// </summary>
public interface ITotpService : IScopedDependency
{
    Task<TotpEnrollment> BeginEnrollmentAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds a still-pending enrollment from the server-side pending credential,
    /// so pages never have to trust a posted secret or otpauth URI. Returns null
    /// when the enrollment does not belong to the user or has expired.
    /// </summary>
    Task<TotpEnrollment?> FindPendingEnrollmentAsync(
        Guid userId,
        string protectedSecret,
        CancellationToken ct = default);

    Task<bool> ConfirmEnrollmentAsync(
        Guid userId,
        string protectedSecret,
        string code,
        CancellationToken ct = default);

    Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Verifies a code against the user's enabled authenticators. Invalid codes
    /// count toward the persistent login lockout, and no code is accepted while
    /// the account is locked.
    /// </summary>
    Task<bool> ValidateAsync(Guid userId, string code, CancellationToken ct = default);

    Task<IReadOnlyList<TotpCredentialSummary>> GetCredentialsAsync(Guid userId, CancellationToken ct = default);

    Task<bool> DisableAsync(Guid userId, Guid credentialId, CancellationToken ct = default);
}
