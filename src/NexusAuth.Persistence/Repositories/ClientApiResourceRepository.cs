using Luck.EntityFrameworkCore.DbContexts;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

/// <summary>
/// ClientApiResource uses composite PK, so it cannot use EfCoreEntityRepository.
/// Uses IUnitOfWork to access DbContext directly.
/// </summary>
public class ClientApiResourceRepository(IUnitOfWork unitOfWork) : IClientApiResourceRepository
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly LuckDbContextBase _dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");

    public async Task<IReadOnlyList<ApiResource>> GetResourcesByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        var resourceIds = await _dbContext.Set<ClientApiResource>()
            .Where(x => x.ClientId == clientId)
            .Select(x => x.ApiResourceId)
            .ToListAsync(ct);

        return await _dbContext.Set<ApiResource>()
            .Where(r => resourceIds.Contains(r.Id))
            .ToListAsync(ct);
    }

    public async Task<Dictionary<Guid, List<Guid>>> GetApiResourceIdsByClientIdsAsync(IEnumerable<Guid> clientIds, CancellationToken ct = default)
    {
        var clientIdList = clientIds.Distinct().ToList();
        if (clientIdList.Count == 0)
            return [];

        var mappings = await _dbContext.Set<ClientApiResource>()
            .Where(x => clientIdList.Contains(x.ClientId))
            .Select(x => new { x.ClientId, x.ApiResourceId })
            .ToListAsync(ct);

        return mappings
            .GroupBy(x => x.ClientId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ApiResourceId).ToList());
    }

    public async Task AddAsync(ClientApiResource association, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(association);

        await InTransactionAsync(async () =>
        {
            var client = await FindClientAsync(association.ClientId, ct);
            var resource = await _dbContext.Set<ApiResource>()
                .FirstOrDefaultAsync(value => value.Id == association.ApiResourceId, ct)
                ?? throw new KeyNotFoundException($"Api resource with id {association.ApiResourceId} not found.");
            var exists = await _dbContext.Set<ClientApiResource>()
                .AnyAsync(value => value.ClientId == association.ClientId && value.ApiResourceId == association.ApiResourceId, ct);

            if (!exists)
                _dbContext.Add(association);

            if (!client.AllowedScopes.Contains(resource.Audience, StringComparer.Ordinal))
            {
                client.Update(allowedScopes: [.. client.AllowedScopes, resource.Audience]);
            }
        }, ct);
    }

    public async Task RemoveAsync(Guid clientId, Guid apiResourceId, CancellationToken ct = default)
    {
        await InTransactionAsync(async () =>
        {
            var client = await FindClientAsync(clientId, ct);
            var resource = await _dbContext.Set<ApiResource>()
                .FirstOrDefaultAsync(value => value.Id == apiResourceId, ct)
                ?? throw new KeyNotFoundException($"Api resource with id {apiResourceId} not found.");
            var entity = await _dbContext.Set<ClientApiResource>()
                .FirstOrDefaultAsync(x => x.ClientId == clientId && x.ApiResourceId == apiResourceId, ct);

            if (entity is not null)
                _dbContext.Set<ClientApiResource>().Remove(entity);

            var remainingScopes = client.AllowedScopes
                .Where(scope => !string.Equals(scope, resource.Audience, StringComparison.Ordinal))
                .ToList();
            if (remainingScopes.Count != client.AllowedScopes.Count)
                client.Update(allowedScopes: remainingScopes);
        }, ct);
    }

    public async Task RemoveByClientIdAsync(Guid clientId, CancellationToken ct = default)
    {
        await InTransactionAsync(async () =>
        {
            await _dbContext.Set<ClientApiResource>()
                .Where(x => x.ClientId == clientId)
                .ExecuteDeleteAsync(ct);
        }, ct);
    }

    public async Task RemoveByApiResourceIdAsync(Guid apiResourceId, CancellationToken ct = default)
    {
        await InTransactionAsync(async () =>
        {
            var resource = await _dbContext.Set<ApiResource>()
                .FirstOrDefaultAsync(value => value.Id == apiResourceId, ct);
            if (resource is null)
            {
                await _dbContext.Set<ClientApiResource>()
                    .Where(x => x.ApiResourceId == apiResourceId)
                    .ExecuteDeleteAsync(ct);
                return;
            }

            // Clear the audience from every client, including legacy rows whose
            // AllowedScopes contains the audience without a join row.
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE nexusauth.oauth_clients
                SET allowed_scopes = allowed_scopes - {resource.Audience}
                WHERE allowed_scopes ? {resource.Audience};
                """, ct);

            await _dbContext.Set<ClientApiResource>()
                .Where(x => x.ApiResourceId == apiResourceId)
                .ExecuteDeleteAsync(ct);
        }, ct);
    }

    private async Task<OAuthClient> FindClientAsync(Guid clientId, CancellationToken ct)
    {
        return await _dbContext.Set<OAuthClient>()
            .Include(client => client.ClientSecrets)
            .FirstOrDefaultAsync(client => client.Id == clientId, ct)
            ?? throw new KeyNotFoundException($"Client with id {clientId} not found.");
    }

    private async Task InTransactionAsync(Func<Task> action, CancellationToken ct)
    {
        var transaction = _dbContext.Database.CurrentTransaction;
        var ownsTransaction = transaction is null;
        if (ownsTransaction)
            transaction = await _dbContext.Database.BeginTransactionAsync(ct);

        try
        {
            await action();
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
