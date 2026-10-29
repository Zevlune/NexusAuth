using Luck.EntityFrameworkCore.DbContexts;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public sealed class ConsentStore(IUnitOfWork unitOfWork) : IConsentStore
{
    private readonly LuckDbContextBase dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    private readonly IUnitOfWork unitOfWork = unitOfWork;

    public async Task<string> CreateRequestAsync(
        Guid userId,
        string query,
        CancellationToken ct)
    {
        var creation = ConsentRequest.Create(userId, query);
        dbContext.Add(creation.Entity);
        await unitOfWork.CommitAsync(ct);
        return creation.RawRequestId;
    }

    public async Task<ConsentRequestState?> GetRequestAsync(
        string id,
        Guid userId,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var requestIdHash = ConsentRequest.HashRequestId(id);
        var now = DateTimeOffset.UtcNow;
        return await dbContext.Set<ConsentRequest>()
            .AsNoTracking()
            .Where(request => request.RequestIdHash == requestIdHash
                && request.UserId == userId
                && request.Status != ConsentRequestStatus.Consumed
                && request.ExpiresAt > now)
            .Select(request => new ConsentRequestState(request.Query, request.Remember))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<bool> ApproveRequestAsync(
        string id,
        Guid userId,
        bool remember,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var requestIdHash = ConsentRequest.HashRequestId(id);
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.Set<ConsentRequest>()
            .Where(request => request.RequestIdHash == requestIdHash
                && request.UserId == userId
                && request.Status == ConsentRequestStatus.Pending
                && request.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(request => request.Status, ConsentRequestStatus.Approved)
                    .SetProperty(request => request.Remember, remember)
                    .SetProperty(request => request.ApprovedAt, now),
                ct);

        return affected == 1;
    }

    public async Task<ConsentRequestState?> ConsumeApprovedRequestAsync(
        string id,
        Guid userId,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var requestIdHash = ConsentRequest.HashRequestId(id);
        var now = DateTimeOffset.UtcNow;
        var state = await dbContext.Set<ConsentRequest>()
            .AsNoTracking()
            .Where(request => request.RequestIdHash == requestIdHash
                && request.UserId == userId
                && request.Status == ConsentRequestStatus.Approved
                && request.ExpiresAt > now)
            .Select(request => new ConsentRequestState(request.Query, request.Remember))
            .SingleOrDefaultAsync(ct);
        if (state is null)
            return null;

        // This one conditional UPDATE is the replay boundary. A concurrent
        // caller waits for the row lock and then observes Approved no longer
        // matching, so only one caller can receive the state.
        var affected = await dbContext.Set<ConsentRequest>()
            .Where(request => request.RequestIdHash == requestIdHash
                && request.UserId == userId
                && request.Status == ConsentRequestStatus.Approved
                && request.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(request => request.Status, ConsentRequestStatus.Consumed)
                    .SetProperty(request => request.ConsumedAt, now),
                ct);

        return affected == 1 ? state : null;
    }

    public async Task<bool> HasGrantAsync(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var scopeNormalized = ConsentGrant.NormalizeScope(scope);
        var claimsMatchKey = ConsentGrant.CreateClaimsMatchKey(claims);
        return await dbContext.Set<ConsentGrant>()
            .AsNoTracking()
            .AnyAsync(grant => grant.UserId == userId
                && grant.ClientId == clientId
                && grant.ScopeNormalized == scopeNormalized
                && grant.ClaimsMatchKey == claimsMatchKey
                && grant.Claims == claims, ct);
    }

    public async Task SaveGrantAsync(
        Guid userId,
        string clientId,
        string scope,
        string? claims,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var grant = ConsentGrant.Create(userId, clientId, scope, claims);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO nexusauth.consent_grants
                (id, user_id, client_id, scope_normalized, claims, claims_match_key, created_at)
            VALUES
                ({grant.Id}, {grant.UserId}, {grant.ClientId}, {grant.ScopeNormalized},
                    {grant.Claims}, {grant.ClaimsMatchKey}, {grant.CreatedAt})
            ON CONFLICT (user_id, client_id, scope_normalized, claims_match_key)
            DO NOTHING;
            """, ct);
    }

    public async Task<IReadOnlyList<ConsentGrantState>> ListGrantsAsync(
        Guid userId,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));

        return await dbContext.Set<ConsentGrant>()
            .AsNoTracking()
            .Where(grant => grant.UserId == userId)
            .OrderBy(grant => grant.ClientId)
            .ThenBy(grant => grant.ScopeNormalized)
            .ThenBy(grant => grant.Claims)
            .Select(grant => new ConsentGrantState(
                grant.ClientId,
                grant.ScopeNormalized,
                grant.Claims))
            .ToListAsync(ct);
    }

    public async Task<int> RevokeGrantsAsync(
        Guid userId,
        string clientId,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user id is required.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        return await dbContext.Set<ConsentGrant>()
            .Where(grant => grant.UserId == userId && grant.ClientId == clientId)
            .ExecuteDeleteAsync(ct);
    }
}
