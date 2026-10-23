using Luck.DDD.Domain.Domain.Entities;

namespace NexusAuth.Domain.Entities;

/// <summary>
/// A bearer credential used by an external SCIM provisioning client.
/// Only the hash of the raw token is persisted.
/// </summary>
public class ScimServicePrincipalCredential : EntityWithIdentity<Guid>
{
    public const int TokenHashLength = 43;
    public const string ScopeRead = "scim:read";
    public const string ScopeWrite = "scim:write";

    /// <summary>
    /// The only scopes the SCIM endpoints understand. Anything else is rejected
    /// instead of being stored as a silently useless grant.
    /// </summary>
    public static IReadOnlyList<string> SupportedScopes { get; } = [ScopeRead, ScopeWrite];

    public string Name { get; private set; } = default!;

    public string TokenHash { get; private set; } = default!;

    public List<string> Scopes { get; private set; } = [];

    public bool IsActive { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    private ScimServicePrincipalCredential(Guid id) : base(id)
    {
    }

    /// <summary>
    /// Creates a credential and returns the raw bearer token exactly once.
    /// </summary>
    public static ScimServicePrincipalCredentialCreationResult Create(
        string name,
        IEnumerable<string>? scopes = null,
        DateTimeOffset? expiresAt = null)
    {
        var rawToken = GenerateRawToken();
        return new(CreateWithToken(name, rawToken, scopes, expiresAt), rawToken);
    }

    /// <summary>
    /// Creates a credential from a caller-supplied raw token. The raw token is
    /// hashed immediately and is never retained by the entity.
    /// </summary>
    public static ScimServicePrincipalCredential CreateWithToken(
        string name,
        string rawToken,
        IEnumerable<string>? scopes = null,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        return new ScimServicePrincipalCredential(Guid.NewGuid())
        {
            Name = name.Trim(),
            TokenHash = Hash(rawToken),
            Scopes = NormalizeScopes(scopes),
            IsActive = true,
            ExpiresAt = expiresAt?.ToUniversalTime(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Hashes a raw bearer token as unpadded Base64Url SHA-256.
    /// </summary>
    public static string Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public bool CanAuthenticate(DateTimeOffset now)
    {
        return IsActive
            && RevokedAt is null
            && (!ExpiresAt.HasValue || ExpiresAt.Value > now);
    }

    public bool CanAuthenticate() => CanAuthenticate(DateTimeOffset.UtcNow);

    public void RecordUse(DateTimeOffset? now = null)
    {
        LastUsedAt = now?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
    }

    public void Revoke(DateTimeOffset? revokedAt = null)
    {
        IsActive = false;
        RevokedAt ??= revokedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Updates mutable metadata. A <c>null</c> scope list keeps the current scopes;
    /// <paramref name="expiresAt"/> follows replace semantics (<c>null</c> = never expires).
    /// </summary>
    public void Update(
        string name,
        IEnumerable<string>? scopes,
        DateTimeOffset? expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var nextScopes = scopes is null ? Scopes : NormalizeScopes(scopes);
        Name = name.Trim();
        Scopes = nextScopes;
        ExpiresAt = expiresAt?.ToUniversalTime();
    }

    /// <summary>
    /// Enables or disables the credential. Disabling is reversible; revocation
    /// (<see cref="Revoke"/>) is terminal and can never be undone here, because a
    /// revoked token may have leaked.
    /// </summary>
    public void SetActive(bool isActive)
    {
        if (isActive && RevokedAt is not null)
            throw new InvalidOperationException("A revoked SCIM credential cannot be re-activated. Create a new credential instead.");

        IsActive = isActive;
    }

    private static List<string> NormalizeScopes(IEnumerable<string>? scopes)
    {
        if (scopes is null)
            return [];

        var normalized = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unsupported = normalized.Where(scope => !SupportedScopes.Contains(scope, StringComparer.Ordinal)).ToArray();
        if (unsupported.Length > 0)
            throw new ArgumentException(
                $"Unsupported SCIM scope(s): {string.Join(", ", unsupported)}. Supported scopes: {string.Join(", ", SupportedScopes)}.",
                nameof(scopes));
        return normalized;
    }

    private static string GenerateRawToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

public sealed record ScimServicePrincipalCredentialCreationResult(
    ScimServicePrincipalCredential Entity,
    string RawToken);
