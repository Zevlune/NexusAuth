using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Domain.Repositories;

/// <summary>
/// A non-blocking, per-user authentication slot held for the duration of one
/// password or authenticator verification. Implementations must release the
/// slot even when the request is cancelled or verification fails.
/// </summary>
public interface IUserAuthenticationAttempt : IAsyncDisposable
{
    User User { get; }
}
