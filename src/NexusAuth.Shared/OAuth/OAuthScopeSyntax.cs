namespace NexusAuth.Shared.OAuth;

/// <summary>
/// Validates the RFC 6749 scope string syntax before normalization.
/// </summary>
public static class OAuthScopeSyntax
{
    public static bool IsValid(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        foreach (var character in scope)
        {
            if (character == ' '
                || character == '\x21'
                || character is >= '\x23' and <= '\x5B'
                || character is >= '\x5D' and <= '\x7E')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
