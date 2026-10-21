using Luck.DDD.Domain.Repositories;
using Luck.Framework.Infrastructure.DependencyInjectionModule;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Domain.Repositories;

public interface IUserExternalIdentityRepository
    : IEntityRepository<UserExternalIdentity, Guid>, IScopedDependency
{
    /// <summary>Atomically creates the user and identity, or returns the concurrent winner.</summary>
    Task<User> GetOrCreateUserAsync(User user, UserExternalIdentity identity, CancellationToken ct = default);

    Task AddAsync(UserExternalIdentity identity, CancellationToken ct = default);

    Task UpdateAsync(UserExternalIdentity identity, CancellationToken ct = default);

    Task<UserExternalIdentity?> FindByIdAsync(Guid id, CancellationToken ct = default);

    Task<UserExternalIdentity?> FindBySubjectAsync(
        string provider,
        string issuer,
        string subject,
        CancellationToken ct = default);

    Task<UserExternalIdentity?> FindByUnionIdAsync(
        string provider,
        string issuer,
        string unionId,
        CancellationToken ct = default);

    Task<IReadOnlyList<UserExternalIdentity>> GetForUserAsync(
        Guid userId,
        CancellationToken ct = default);
}
