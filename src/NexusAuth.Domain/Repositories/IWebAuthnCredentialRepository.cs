using NexusAuth.Domain.Entities;

namespace NexusAuth.Domain.Repositories;

public interface IWebAuthnCredentialRepository : IEntityRepository<WebAuthnCredential, Guid>, IScopedDependency
{
    Task AddAsync(WebAuthnCredential credential, CancellationToken ct = default);
    Task<WebAuthnCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct = default);
    Task<IReadOnlyList<WebAuthnCredential>> GetEnabledForUserAsync(Guid userId, CancellationToken ct = default);
    Task UpdateAsync(WebAuthnCredential credential, CancellationToken ct = default);
    Task<IReadOnlyList<WebAuthnCredential>> GetForUserAsync(Guid userId, CancellationToken ct = default);
    Task<bool> RenameAsync(Guid userId, Guid id, string displayName, CancellationToken ct = default);
    Task<bool> DisableAsync(Guid userId, Guid id, DateTimeOffset now, CancellationToken ct = default);
}
