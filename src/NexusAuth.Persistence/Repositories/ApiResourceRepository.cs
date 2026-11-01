using Luck.EntityFrameworkCore.DbContexts;
using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public class ApiResourceRepository : EfCoreAggregateRootRepository<ApiResource, Guid>, IApiResourceRepository
{
    private readonly IUnitOfWork _unitOfWork;

    public ApiResourceRepository(IUnitOfWork unitOfWork) : base(unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// 按资源名称查询单个 API 资源。
    /// </summary>
    public async Task<ApiResource?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        return await FindAll(r => r.Name == name).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 按资源名称批量查询 API 资源。
    /// </summary>
    public async Task<IReadOnlyList<ApiResource>> FindByNamesAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var nameSet = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (nameSet.Length == 0)
            return [];

        return await FindAll(r => nameSet.Contains(r.Name)).ToListAsync(ct);
    }

    public async Task<ApiResource?> FindByAudienceAsync(string audience, CancellationToken ct = default)
    {
        return await FindAll(r => r.Audience == audience).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ApiResource>> FindByAudiencesAsync(IEnumerable<string> audiences, CancellationToken ct = default)
    {
        var audienceSet = audiences
            .Where(audience => !string.IsNullOrWhiteSpace(audience))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (audienceSet.Length == 0)
            return [];

        return await FindAll(r => audienceSet.Contains(r.Audience)).ToListAsync(ct);
    }

    /// <summary>
    /// 按主键查询 API 资源。
    /// </summary>
    public async Task<ApiResource?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await FindAsync(id);
    }

    public async Task<IReadOnlyList<ApiResource>> FindByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.Distinct().ToArray();
        if (idSet.Length == 0)
            return [];

        return await FindAll(resource => idSet.Contains(resource.Id)).ToListAsync(ct);
    }

    public async Task<(List<ApiResource> Items, int Total)> GetPagedAsync(
        string? keyword,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = FindAll();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            query = query.Where(r => r.Name.Contains(kw)
                || r.DisplayName.Contains(kw)
                || r.Audience.Contains(kw));
        }

        if (isActive.HasValue)
        {
            query = query.Where(r => r.IsActive == isActive.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    /// <summary>
    /// 获取全部启用状态的 API 资源。
    /// </summary>
    public async Task<IReadOnlyList<ApiResource>> GetAllActiveAsync(CancellationToken ct = default)
    {
        return await FindAll(r => r.IsActive).ToListAsync(ct);
    }

    /// <summary>
    /// 新增 API 资源并提交事务。
    /// </summary>
    public async Task AddAsync(ApiResource resource, CancellationToken ct = default)
    {
        Add(resource);
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task UpdateAsync(ApiResource resource, CancellationToken ct = default)
    {
        Update(resource);
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task DeleteAsync(ApiResource resource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var dbContext = _unitOfWork.GetLuckDbContext() as LuckDbContextBase
            ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
        var transaction = dbContext.Database.CurrentTransaction;
        var ownsTransaction = transaction is null;
        if (ownsTransaction)
            transaction = await dbContext.Database.BeginTransactionAsync(ct);

        try
        {
            // Clear the audience from every client, including legacy rows whose
            // AllowedScopes contains the audience without a join row.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE nexusauth.oauth_clients
                SET allowed_scopes = allowed_scopes - {resource.Audience}
                WHERE allowed_scopes ? {resource.Audience};
                """, ct);

            await dbContext.Set<ClientApiResource>()
                .Where(value => value.ApiResourceId == resource.Id)
                .ExecuteDeleteAsync(ct);
            Remove(resource);
            await _unitOfWork.CommitAsync(ct);
            if (ownsTransaction)
                await transaction!.CommitAsync(ct);
        }
        catch
        {
            if (ownsTransaction)
                await transaction!.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (ownsTransaction)
                await transaction!.DisposeAsync();
        }
    }
}
