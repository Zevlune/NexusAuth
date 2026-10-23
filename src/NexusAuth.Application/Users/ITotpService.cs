namespace NexusAuth.Application.Users;

/// <summary>
/// TOTP enrollment and verification operations used by the login flow.
/// </summary>
public interface ITotpService : IScopedDependency
{
    Task<TotpEnrollment> BeginEnrollmentAsync(Guid userId, CancellationToken ct = default);

    Task<bool> ConfirmEnrollmentAsync(
        Guid userId,
        string protectedSecret,
        string code,
        CancellationToken ct = default);

    Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default);

    Task<bool> ValidateAsync(Guid userId, string code, CancellationToken ct = default);

    Task<IReadOnlyList<TotpCredentialSummary>> GetCredentialsAsync(Guid userId, CancellationToken ct = default);

    Task<bool> DisableAsync(Guid userId, Guid credentialId, CancellationToken ct = default);
}
