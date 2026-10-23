namespace NexusAuth.Application.Users;

/// <summary>
/// Enrollment material. ProtectedSecret is the Data Protection ciphertext
/// that must be supplied when confirming the enrollment; ManualEntryKey is
/// the Base32 value shown to an authenticator and OtpauthUri is its QR payload.
/// </summary>
public sealed record TotpEnrollment(
    Guid CredentialId,
    string ProtectedSecret,
    string ManualEntryKey,
    string OtpauthUri);
