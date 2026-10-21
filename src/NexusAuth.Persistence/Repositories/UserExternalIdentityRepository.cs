using Luck.EntityFrameworkCore.DbContexts;
using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Domain.AggregateRoots.Users;
using Npgsql;

namespace NexusAuth.Persistence.Repositories;

public sealed class UserExternalIdentityRepository(IUnitOfWork unitOfWork)
    : EfCoreEntityRepository<UserExternalIdentity, Guid>(unitOfWork), IUserExternalIdentityRepository
{
    private readonly LuckDbContextBase dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    private readonly IUnitOfWork unitOfWork = unitOfWork;

    public async Task<User> GetOrCreateUserAsync(User user, UserExternalIdentity identity, CancellationToken ct = default)
    {
        if (identity.UserId != user.Id)
            throw new ArgumentException("The identity must belong to the new user.", nameof(identity));

        // SaveChanges wraps both inserts in one transaction. A unique identity
        // conflict rolls back the new user too, so concurrent logins leave no orphans.
        dbContext.AddRange(user, identity);
        try
        {
            await unitOfWork.CommitAsync(ct);
            return user;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(identity).State = EntityState.Detached;
            dbContext.Entry(user).State = EntityState.Detached;
            var winner = await FindBySubjectAsync(identity.Provider, identity.Issuer, identity.Subject, ct);
            if (winner is null)
                throw;
            return await dbContext.Set<User>().SingleAsync(value => value.Id == winner.UserId, ct);
        }
    }

    public async Task AddAsync(UserExternalIdentity identity, CancellationToken ct = default)
    {
        dbContext.Add(identity);
        await unitOfWork.CommitAsync(ct);
    }

    public async Task UpdateAsync(UserExternalIdentity identity, CancellationToken ct = default)
    {
        // Concurrent logins must never move the stored authentication time backwards.
        await dbContext.Set<UserExternalIdentity>()
            .Where(value => value.Id == identity.Id && value.LastAuthenticatedAt < identity.LastAuthenticatedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.LastAuthenticatedAt, identity.LastAuthenticatedAt), ct);
    }

    public async Task<UserExternalIdentity?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await FindAsync(id);

    public Task<UserExternalIdentity?> FindBySubjectAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken ct = default)
    {
        provider = NormalizeRequired(provider);
        issuer = NormalizeRequired(issuer);
        subject = NormalizeRequired(subject);
        return FindAll(identity => identity.Provider == provider
                && identity.Issuer == issuer
                && identity.Subject == subject)
            .FirstOrDefaultAsync(ct);
    }

    public Task<UserExternalIdentity?> FindByUnionIdAsync(
        string provider,
        string issuer,
        string unionId,
        CancellationToken ct = default)
    {
        provider = NormalizeRequired(provider);
        issuer = NormalizeRequired(issuer);
        unionId = NormalizeRequired(unionId);
        return FindAll(identity => identity.Provider == provider
                && identity.Issuer == issuer
                && identity.UnionId == unionId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<UserExternalIdentity>> GetForUserAsync(
        Guid userId,
        CancellationToken ct = default) =>
        await FindAll(identity => identity.UserId == userId)
            .OrderBy(identity => identity.Provider)
            .ThenBy(identity => identity.Issuer)
            .ThenBy(identity => identity.CreatedAt)
            .ToListAsync(ct);

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
