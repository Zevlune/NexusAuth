using System.Security.Cryptography;
using System.Text;
using Luck.DDD.Domain.Domain.Entities;

namespace NexusAuth.Domain.Entities;

public sealed class ConsentGrant : EntityWithIdentity<Guid>
{
    public Guid UserId { get; private set; }

    public string ClientId { get; private set; } = default!;

    public string ScopeNormalized { get; private set; } = default!;

    /// <summary>
    /// The exact claims value accepted by the user. No JSON or whitespace
    /// normalization is applied.
    /// </summary>
    public string? Claims { get; private set; }

    /// <summary>
    /// A deterministic match key for the exact claims value. It allows a
    /// database unique index to distinguish null from every string value.
    /// </summary>
    public string ClaimsMatchKey { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    private ConsentGrant(Guid id) : base(id)
    {
    }

    public static ConsentGrant Create(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        DateTimeOffset? createdAt = null)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        return new ConsentGrant(Guid.NewGuid())
        {
            UserId = userId,
            ClientId = clientId,
            ScopeNormalized = NormalizeScope(scope),
            Claims = claims,
            ClaimsMatchKey = CreateClaimsMatchKey(claims),
            CreatedAt = (createdAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        };
    }

    /// <summary>
    /// Normalizes only the syntactic scope representation: whitespace is
    /// collapsed, duplicate tokens are removed, and the remaining
    /// case-sensitive tokens are sorted ordinally.
    /// </summary>
    public static string NormalizeScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        try
        {
            return string.Join(
                ' ',
                scope
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            throw new ArgumentException("The scope value is invalid.", nameof(scope), exception);
        }
    }

    public static string CreateClaimsMatchKey(string? claims)
    {
        if (claims is null)
            return "n";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(claims));
        var encoded = Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"v:{encoded}";
    }
}
