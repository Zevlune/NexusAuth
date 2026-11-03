namespace NexusAuth.Application.Users;

/// <summary>
/// Normalizes a second-factor value typed by a user and decides whether it is
/// a six-digit authenticator code or a single-use recovery code.
/// </summary>
public static class SecondFactorCode
{
    /// <summary>
    /// Removes all whitespace. Authenticator apps display codes as "123 456"
    /// and pasted values often carry trailing spaces; neither TOTP codes nor
    /// recovery codes contain meaningful whitespace.
    /// </summary>
    public static string Normalize(string? code) =>
        string.IsNullOrEmpty(code) ? string.Empty : string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>True when the normalized value has the six-digit TOTP format.</summary>
    public static bool IsTotp(string normalizedCode) =>
        normalizedCode.Length == 6 && normalizedCode.All(char.IsAsciiDigit);
}
