using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Domain.Repositories;

/// <summary>
/// ClientApiResource uses composite PK (no surrogate Id), so it does not extend IEntityRepository.
/// </summary>
public interface IClientApiResourceRepository : IScopedDependency
{
    Task<IReadOnlyList<ApiResource>> GetResourcesByClientIdAsync(Guid clientId, CancellationToken ct = default);

    Task<Dictionary<Guid, List<Guid>>> GetApiResourceIdsByClientIdsAsync(IEnumerable<Guid> clientIds, CancellationToken ct = default);

    /// <summary>
    /// Adds an association idempotently and keeps the client's resource
    /// audience in AllowedScopes.
    /// </summary>
    Task AddAsync(ClientApiResource association, CancellationToken ct = default);

    /// <summary>
    /// Removes an association idempotently and removes its audience from the
    /// client's AllowedScopes.
    /// </summary>
    Task RemoveAsync(Guid clientId, Guid apiResourceId, CancellationToken ct = default);

    Task RemoveByApiResourceIdAsync(Guid apiResourceId, CancellationToken ct = default);

    /// <summary>
    /// 中文注释：删除某个客户端的全部 API 资源关联；数据库外键也会在客户端删除时级联清理。
    /// </summary>
    Task RemoveByClientIdAsync(Guid clientId, CancellationToken ct = default);
}
