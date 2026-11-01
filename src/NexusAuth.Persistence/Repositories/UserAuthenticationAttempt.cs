using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using Npgsql;

namespace NexusAuth.Persistence.Repositories;

/// <summary>
/// Holds a PostgreSQL session advisory lock for one user authentication
/// attempt. The lock is deliberately non-blocking so a burst cannot consume
/// an unbounded number of database connections waiting for one account.
/// </summary>
internal sealed class UserAuthenticationAttempt(
    NpgsqlConnection connection,
    Guid userId,
    User user) : IUserAuthenticationAttempt
{
    internal const long AdvisoryLockSeed = 0x4E58534155544831;
    internal const string TryLockSql =
        "SELECT pg_try_advisory_lock(hashtextextended(@user_id::text, @seed));";
    private const string UnlockSql =
        "SELECT pg_advisory_unlock(hashtextextended(@user_id::text, @seed));";

    private NpgsqlConnection? heldConnection = connection;

    public User User { get; } = user;

    public async ValueTask DisposeAsync()
    {
        var current = Interlocked.Exchange(ref heldConnection, null);
        if (current is null)
            return;

        await ReleaseAsync(current, userId);
    }

    internal static async ValueTask ReleaseAsync(NpgsqlConnection connection, Guid userId)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = UnlockSql;
            command.Parameters.AddWithValue("user_id", userId.ToString("D"));
            command.Parameters.AddWithValue("seed", AdvisoryLockSeed);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch
        {
            // Closing the session releases any remaining advisory lock. Cleanup
            // must not replace an authentication result or cancellation error.
        }
        finally
        {
            try
            {
                await connection.CloseAsync();
            }
            catch
            {
                // Ignore cleanup failures after the lock has been released or
                // the connection is already unusable.
            }

            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // Cleanup must not replace an authentication result.
            }
        }
    }
}
