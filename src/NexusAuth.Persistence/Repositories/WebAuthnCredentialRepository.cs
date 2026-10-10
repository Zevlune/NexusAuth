using Luck.EntityFrameworkCore.DbContexts;
using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public sealed class WebAuthnCredentialRepository(IUnitOfWork unitOfWork)
    : EfCoreEntityRepository<WebAuthnCredential, Guid>(unitOfWork), IWebAuthnCredentialRepository
{
    private readonly LuckDbContextBase _dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    private readonly IUnitOfWork _unitOfWork = unitOfWork;

    public async Task AddAsync(WebAuthnCredential credential, CancellationToken ct = default)
    {
        _dbContext.Add(credential);
        await _unitOfWork.CommitAsync(ct);
    }

    public Task<WebAuthnCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        return FindAll(credential => credential.CredentialId.SequenceEqual(credentialId)).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<WebAuthnCredential>> GetEnabledForUserAsync(Guid userId, CancellationToken ct = default) =>
        await FindAll(credential => credential.UserId == userId && credential.DisabledAt == null)
            .OrderBy(credential => credential.CreatedAt)
            .ToListAsync(ct);

    public async Task UpdateAsync(WebAuthnCredential credential, CancellationToken ct = default)
    {
        // Authentication must never write a stale DisabledAt/DisplayName back
        // over an administrator's revocation or the owner's rename.
        var changed = await FindAll(item => item.Id == credential.Id && item.UserId == credential.UserId && item.DisabledAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.SignatureCounter, credential.SignatureCounter)
                .SetProperty(item => item.IsBackedUp, credential.IsBackedUp)
                .SetProperty(item => item.LastUsedAt, credential.LastUsedAt), ct);
        if (changed != 1)
            throw new InvalidOperationException("The passkey was revoked during authentication.");
    }

    public async Task<IReadOnlyList<WebAuthnCredential>> GetForUserAsync(Guid userId, CancellationToken ct = default) =>
        await FindAll(credential => credential.UserId == userId).AsNoTracking()
            .OrderByDescending(credential => credential.CreatedAt).ToListAsync(ct);

    public async Task<bool> RenameAsync(Guid userId, Guid id, string displayName, CancellationToken ct = default) =>
        await FindAll(credential => credential.UserId == userId && credential.Id == id && credential.DisabledAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(credential => credential.DisplayName, displayName), ct) == 1;

    public async Task<bool> DisableAsync(Guid userId, Guid id, DateTimeOffset now, CancellationToken ct = default) =>
        await FindAll(credential => credential.UserId == userId && credential.Id == id && credential.DisabledAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(credential => credential.DisabledAt, now), ct) == 1;
}
