using Luck.DDD.Domain.Repositories;
using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Domain.Repositories;

public interface IScimServicePrincipalCredentialRepository
    : IEntityRepository<ScimServicePrincipalCredential, Guid>, IScopedDependency
{
    Task<ScimServicePrincipalCredential?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken ct = default);

    Task<ScimServicePrincipalCredential?> FindByNameAsync(
        string name,
        CancellationToken ct = default);

    Task<ScimServicePrincipalCredential?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ScimServicePrincipalCredential>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(
        ScimServicePrincipalCredential credential,
        CancellationToken ct = default);

    Task UpdateAsync(
        ScimServicePrincipalCredential credential,
        CancellationToken ct = default);

    Task RecordUseAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Terminal revocation as one UPDATE without the xmin check, so it cannot
    /// lose a race against concurrent LastUsedAt writes from token use. The
    /// first revocation time is kept. Returns false when the id is unknown.
    /// </summary>
    Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default);
}
