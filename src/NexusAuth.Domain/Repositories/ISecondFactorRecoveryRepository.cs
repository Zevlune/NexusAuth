using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.Entities;

namespace NexusAuth.Domain.Repositories;

/// <summary>
/// Persistence boundary for one-time second-factor recovery codes and their
/// administrative reset audit records.
/// </summary>
public interface ISecondFactorRecoveryRepository : IScopedDependency
{
    Task ReplaceAsync(
        Guid userId,
        IReadOnlyCollection<UserRecoveryCode> codes,
        CancellationToken ct = default);

    Task<bool> TryRedeemAsync(
        Guid userId,
        string codeHash,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task DeleteForUserAsync(Guid userId, CancellationToken ct = default);

    Task AddAuditAsync(SecondFactorRecoveryAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Executes the supplied operation in the scoped application database
    /// transaction. The callback makes the reset operation atomic across user
    /// credentials, passkeys, tokens, sessions, recovery codes and its audit.
    /// </summary>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken ct = default);
}
