using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public class OAuthClientRepository : EfCoreAggregateRootRepository<OAuthClient, Guid>, IOAuthClientRepository
{
    private readonly IUnitOfWork _unitOfWork;

    public OAuthClientRepository(IUnitOfWork unitOfWork) : base(unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<OAuthClient?> FindByClientIdAsync(string clientId, CancellationToken ct = default)
    {
        return await FindAll(c => c.ClientId == clientId)
            .Include(c => c.ClientSecrets)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(OAuthClient client, CancellationToken ct = default)
    {
        Add(client);
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task UpdateAsync(OAuthClient client, CancellationToken ct = default)
    {
        // Clients returned by this repository are tracked. Calling Update on
        // the aggregate would mark a newly appended OAuthClientSecret as
        // Modified, so first-time Workbench credential initialization tries
        // to update a row that does not exist.
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task ReplaceSharedSecretAsync(Guid clientId, OAuthClientSecret secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.ClientId != clientId)
            throw new ArgumentException("The secret belongs to another OAuth client.", nameof(secret));

        var dbContext = _unitOfWork.GetLuckDbContext() as LuckDbContextBase
            ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
        await dbContext.Set<OAuthClientSecret>()
            .Where(item => item.ClientId == clientId
                && item.Type == OAuthClientSecret.TypeSharedSecret
                && item.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false), ct);
        dbContext.Add(secret);
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task DeleteAsync(OAuthClient client, CancellationToken ct = default)
    {
        var dbContext = _unitOfWork.GetLuckDbContext() as LuckDbContextBase
            ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
        var transaction = dbContext.Database.CurrentTransaction;
        var ownsTransaction = transaction is null;
        if (ownsTransaction)
            transaction = await dbContext.Database.BeginTransactionAsync(ct);

        try
        {
            await dbContext.Set<AuthorizationCode>()
                .Where(code => code.ClientId == client.ClientId && !code.IsUsed)
                .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.IsUsed, true), ct);
            await dbContext.Set<RefreshToken>()
                .Where(token => token.ClientId == client.ClientId && !token.IsRevoked)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.IsRevoked, true), ct);
            await dbContext.Set<DeviceAuthorization>()
                .Where(device => device.ClientId == client.ClientId
                    && (device.Status == DeviceAuthorizationStatus.Pending
                        || device.Status == DeviceAuthorizationStatus.Approved))
                .ExecuteUpdateAsync(setters => setters.SetProperty(device => device.Status, DeviceAuthorizationStatus.Denied), ct);
            await dbContext.Set<ConsentGrant>()
                .Where(grant => grant.ClientId == client.ClientId)
                .ExecuteDeleteAsync(ct);
            await dbContext.Set<ClientApiResource>()
                .Where(association => association.ClientId == client.Id)
                .ExecuteDeleteAsync(ct);

            Remove(client);
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

    public async Task<(List<OAuthClient> Items, int Total)> GetPagedAsync(
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
            query = query.Where(c => c.ClientId.Contains(kw) || c.ClientName.Contains(kw));
        }

        if (isActive.HasValue)
        {
            query = query.Where(c => c.IsActive == isActive.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(c => c.ClientSecrets)
            .OrderByDescending(c => c.CreatedAt)
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<OAuthClient?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await FindAll(c => c.Id == id)
            .Include(c => c.ClientSecrets)
            .FirstOrDefaultAsync(ct);
    }
}
