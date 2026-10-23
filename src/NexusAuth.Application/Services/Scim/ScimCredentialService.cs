namespace NexusAuth.Application.Services.Scim;

public class ScimCredentialService(IScimServicePrincipalCredentialRepository credentialRepository) : IScimCredentialService
{
    public async Task<bool> ValidateAsync(string rawToken, string requiredScope, CancellationToken ct = default) =>
        await AuthorizeAsync(rawToken, requiredScope, ct) == ScimCredentialValidationResult.Granted;

    public async Task<ScimCredentialValidationResult> AuthorizeAsync(string rawToken, string requiredScope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return ScimCredentialValidationResult.InvalidToken;
        var credential = await credentialRepository.FindByTokenHashAsync(ScimServicePrincipalCredential.Hash(rawToken), ct);
        if (credential is null || !credential.CanAuthenticate()) return ScimCredentialValidationResult.InvalidToken;
        if (!credential.Scopes.Contains(requiredScope, StringComparer.Ordinal)) return ScimCredentialValidationResult.InsufficientScope;
        credential.RecordUse();
        await credentialRepository.UpdateAsync(credential, ct);
        return ScimCredentialValidationResult.Granted;
    }

    public async Task<ScimCredentialCreated> CreateAsync(string name, IReadOnlyCollection<string>? scopes, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow) throw new ArgumentException("expiresAt must be in the future.");
        EnsureScopesNotEmpty(scopes);
        if (await credentialRepository.FindByNameAsync(name.Trim(), ct) is not null) throw new InvalidOperationException("A SCIM credential with this name already exists.");
        var created = ScimServicePrincipalCredential.Create(name, scopes ?? ScimServicePrincipalCredential.SupportedScopes, expiresAt);
        await credentialRepository.AddAsync(created.Entity, ct);
        return new ScimCredentialCreated(Map(created.Entity), created.RawToken);
    }

    public async Task<IReadOnlyList<ScimCredentialSummary>> GetAllAsync(CancellationToken ct = default) => (await credentialRepository.GetAllAsync(ct)).Select(Map).ToArray();

    /// <summary>
    /// Updates a credential. <paramref name="scopes"/> = null keeps the current scopes;
    /// <paramref name="expiresAt"/> = null means "never expires" (the dashboard's empty expiry field).
    /// A revoked credential can never be re-activated.
    /// </summary>
    public async Task<ScimCredentialSummary?> UpdateAsync(Guid id, string name, IReadOnlyCollection<string>? scopes, DateTimeOffset? expiresAt, bool isActive, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureScopesNotEmpty(scopes);
        var credential = await credentialRepository.FindByIdAsync(id, ct);
        if (credential is null) return null;
        // 中文注释：编辑已过期凭证时表单会原样回传旧的过期时间，只有“改成”过去时间才拒绝。
        if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow && !IsSameInstant(expiry, credential.ExpiresAt))
            throw new ArgumentException("expiresAt must be in the future.");
        if (isActive && credential.RevokedAt is not null)
            throw new InvalidOperationException("A revoked SCIM credential cannot be re-activated. Create a new credential instead.");
        var sameName = await credentialRepository.FindByNameAsync(name.Trim(), ct);
        if (sameName is not null && sameName.Id != id) throw new InvalidOperationException("A SCIM credential with this name already exists.");
        credential.Update(name, scopes, expiresAt);
        credential.SetActive(isActive);
        await credentialRepository.UpdateAsync(credential, ct);
        return Map(credential);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var credential = await credentialRepository.FindByIdAsync(id, ct);
        if (credential is null) return false;
        credential.Revoke();
        await credentialRepository.UpdateAsync(credential, ct);
        return true;
    }

    private static void EnsureScopesNotEmpty(IReadOnlyCollection<string>? scopes)
    {
        if (scopes is not null && scopes.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one SCIM scope is required.", nameof(scopes));
    }

    // The stored value has microsecond precision and the client may round-trip it with millisecond precision.
    private static bool IsSameInstant(DateTimeOffset value, DateTimeOffset? current) =>
        current is { } stored && (value - stored).Duration() < TimeSpan.FromSeconds(1);

    private static ScimCredentialSummary Map(ScimServicePrincipalCredential credential) => new(credential.Id, credential.Name, credential.Scopes, credential.IsActive, credential.ExpiresAt, credential.LastUsedAt, credential.CreatedAt, credential.RevokedAt);
}
