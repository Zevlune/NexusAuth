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

    Task AddAsync(ClientApiResource association, CancellationToken ct = default);

    Task RemoveAsync(Guid clientId, Guid apiResourceId, CancellationToken ct = default);

    Task RemoveByApiResourceIdAsync(Guid apiResourceId, CancellationToken ct = default);

    /// <summary>
    /// 中文注释：删除某个客户端的全部 API 资源关联（client_api_resources 没有外键级联）。
    /// </summary>
    Task RemoveByClientIdAsync(Guid clientId, CancellationToken ct = default);
}
