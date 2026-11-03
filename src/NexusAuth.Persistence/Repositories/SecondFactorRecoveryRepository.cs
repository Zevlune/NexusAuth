using Luck.EntityFrameworkCore.DbContexts;
using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Persistence.Repositories;

public sealed class SecondFactorRecoveryRepository(IUnitOfWork unitOfWork)
    : EfCoreEntityRepository<UserRecoveryCode, Guid>(unitOfWork), ISecondFactorRecoveryRepository
{
    private readonly LuckDbContextBase dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
        ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    private readonly IUnitOfWork unitOfWork = unitOfWork;

    public async Task ReplaceAsync(
        Guid userId,
        IReadOnlyCollection<UserRecoveryCode> codes,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Any(code => code.UserId != userId))
            throw new ArgumentException("All recovery codes must belong to the requested user.", nameof(codes));

        await ExecuteInTransactionAsync(async transactionCt =>
        {
            await dbContext.Set<UserRecoveryCode>()
                .Where(code => code.UserId == userId)
                .ExecuteDeleteAsync(transactionCt);
            if (codes.Count == 0)
                return;

            dbContext.AddRange(codes);
            await unitOfWork.CommitAsync(transactionCt);
        }, ct);
    }

    public async Task<bool> TryRedeemAsync(
        Guid userId,
        string codeHash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        now = now.ToUniversalTime();

        var affected = await dbContext.Set<UserRecoveryCode>()
            .Where(code => code.UserId == userId
                && code.CodeHash == codeHash
                && code.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(code => code.UsedAt, now), ct);
        return affected == 1;
    }

    public Task DeleteForUserAsync(Guid userId, CancellationToken ct = default) =>
        dbContext.Set<UserRecoveryCode>()
            .Where(code => code.UserId == userId)
            .ExecuteDeleteAsync(ct);

    public async Task AddAuditAsync(SecondFactorRecoveryAudit audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        dbContext.Add(audit);
        await unitOfWork.CommitAsync(ct);
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var transaction = dbContext.Database.CurrentTransaction;
        var ownsTransaction = transaction is null;
        if (ownsTransaction)
            transaction = await dbContext.Database.BeginTransactionAsync(ct);

        try
        {
            await operation(ct);
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
