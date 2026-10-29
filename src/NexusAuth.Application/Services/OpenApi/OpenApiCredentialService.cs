using NexusAuth.Application.Clients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Application.Services.OpenApi;

public sealed class OpenApiCredentialService(IOpenApiCredentialRepository credentialRepository) : IOpenApiCredentialService
{
    public async Task<bool> ValidateAsync(string rawToken, string targetType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return false;
        var normalizedTargetType = OpenApiCredential.NormalizeTargetType(targetType);
        var credential = await credentialRepository.FindByTokenHashAsync(OpenApiCredential.Hash(rawToken), ct);
        if (credential is null || !credential.CanAuthenticate(normalizedTargetType, OpenApiCredential.GetReadScope(normalizedTargetType))) return false;
        credential.RecordUse();
        await credentialRepository.UpdateAsync(credential, ct);
        return true;
    }

    public async Task<OpenApiCredentialCreated> CreateAsync(string name, string targetType, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow) throw new ArgumentException("expiresAt must be in the future.");
        if (await credentialRepository.FindByNameAsync(name, ct) is not null) throw new WorkbenchBusinessConflictException("An Open API credential with this name already exists.");
        var created = OpenApiCredential.Create(name, targetType, ToUtc(expiresAt));
        await credentialRepository.AddAsync(created.Entity, ct);
        return new(Map(created.Entity), created.Token);
    }

    public async Task<IReadOnlyList<OpenApiCredentialSummary>> GetAllAsync(CancellationToken ct = default) => (await credentialRepository.GetAllAsync(ct)).Select(Map).ToArray();
    /// <summary>
    /// Updates a credential. <paramref name="expiresAt"/> = null means "never expires" (the dashboard's empty
    /// expiry field). Disabling is reversible; a revoked credential can never be re-activated.
    /// </summary>
    public async Task<OpenApiCredentialSummary?> UpdateAsync(Guid id, string name, DateTimeOffset? expiresAt, bool isActive, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var credential = await credentialRepository.FindByIdAsync(id, ct);
        if (credential is null) return null;
        // 中文注释：编辑已过期凭证时表单会原样回传旧的过期时间，只有“改成”过去时间才拒绝。
        if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow && !IsSameInstant(expiry, credential.ExpiresAt))
            throw new ArgumentException("expiresAt must be in the future.");
        if (isActive && credential.RevokedAt is not null)
            throw new WorkbenchBusinessConflictException("A revoked Open API credential cannot be re-activated. Create a new credential instead.");
        var sameName = await credentialRepository.FindByNameAsync(name.Trim(), ct);
        if (sameName is not null && sameName.Id != id) throw new WorkbenchBusinessConflictException("An Open API credential with this name already exists.");
        credential.Update(name, ToUtc(expiresAt), isActive);
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
    private static OpenApiCredentialSummary Map(OpenApiCredential item) => new(item.Id, item.Name, item.TargetType, item.Scopes, item.IsActive, item.ExpiresAt, item.LastUsedAt, item.CreatedAt, item.RevokedAt);
    private static DateTimeOffset? ToUtc(DateTimeOffset? value) => value?.ToUniversalTime();
    // The stored value has microsecond precision and the client may round-trip it with millisecond precision.
    private static bool IsSameInstant(DateTimeOffset value, DateTimeOffset? current) =>
        current is { } stored && (value - stored).Duration() < TimeSpan.FromSeconds(1);
}
