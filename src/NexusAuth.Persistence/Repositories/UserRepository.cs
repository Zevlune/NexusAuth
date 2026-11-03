using Luck.EntityFrameworkCore.Repositories;
using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Repositories;
using Npgsql;

namespace NexusAuth.Persistence.Repositories;

public class UserRepository : EfCoreAggregateRootRepository<User, Guid>, IUserRepository
{
    private readonly LuckDbContextBase _dbContext;
    private readonly IUnitOfWork _unitOfWork;

    public UserRepository(IUnitOfWork unitOfWork) : base(unitOfWork)
    {
        _unitOfWork = unitOfWork;
        _dbContext = unitOfWork.GetLuckDbContext() as LuckDbContextBase
            ?? throw new InvalidOperationException("Failed to resolve LuckDbContext.");
    }

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        return await FindAll(u => u.Username == username).FirstOrDefaultAsync(ct);
    }

    public async Task<User?> FindByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        return await FindAll(u => u.ExternalId == externalId.Trim()).FirstOrDefaultAsync(ct);
    }

    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.ToLowerInvariant();
        return await FindAll(u => u.Email == normalizedEmail).FirstOrDefaultAsync(ct);
    }

    public async Task<User?> FindByPhoneNumberAsync(string phoneNumber, CancellationToken ct = default)
    {
        return await FindAll(u => u.PhoneNumber == phoneNumber).FirstOrDefaultAsync(ct);
    }

    public async Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await FindAsync(id);
    }

    public async Task<IUserAuthenticationAttempt?> BeginAuthenticationAttemptAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var connectionString = _dbContext.Database.GetConnectionString()
            ?? _dbContext.Database.GetDbConnection().ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The authentication lock requires a PostgreSQL connection string.");

        var connection = new NpgsqlConnection(connectionString);
        var opened = false;
        try
        {
            await connection.OpenAsync(ct);
            opened = true;
            if (!await AcquireAuthenticationLockAsync(connection, userId, ct))
            {
                await UserAuthenticationAttempt.ReleaseAsync(connection, userId);
                return null;
            }

            var user = await ReloadUserAsync(userId, ct);
            if (user is null)
            {
                await UserAuthenticationAttempt.ReleaseAsync(connection, userId);
                return null;
            }

            return new UserAuthenticationAttempt(connection, userId, user);
        }
        catch
        {
            if (opened)
                await UserAuthenticationAttempt.ReleaseAsync(connection, userId);
            else
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch
                {
                    // Preserve the original open/cancellation exception.
                }
            }

            throw;
        }
    }

    public async Task<(IReadOnlyList<User> Items, int Total)> GetScimPagedAsync(
        string? userName,
        string? externalId,
        bool? isActive,
        string? email,
        int startIndex,
        int count,
        CancellationToken ct = default)
    {
        // 中文注释：系统账号（管理员）不通过 SCIM 暴露。
        var query = FindAll(user => !user.IsSystemAccount);
        if (!string.IsNullOrWhiteSpace(userName))
            query = query.Where(user => user.Username == userName);
        if (!string.IsNullOrWhiteSpace(externalId))
            query = query.Where(user => user.ExternalId == externalId);
        if (isActive.HasValue)
            query = query.Where(user => user.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(email))
            query = query.Where(user => user.Email == email.ToLowerInvariant());

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(user => user.Id)
            .Skip(startIndex - 1)
            .Take(count)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<(IReadOnlyList<User> Items, int Total)> GetAdminPagedAsync(
        string? keyword,
        bool? isActive,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = FindAll();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var value = keyword.Trim();
            query = query.Where(user => user.Username.Contains(value)
                || user.Nickname.Contains(value)
                || (user.Email != null && user.Email.Contains(value))
                || (user.PhoneNumber != null && user.PhoneNumber.Contains(value))
                || (user.ExternalId != null && user.ExternalId.Contains(value)));
        }
        if (isActive.HasValue)
            query = query.Where(user => user.IsActive == isActive.Value);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(user => user.CreatedAt)
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        Add(user);
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Callers load users through this repository, so EF is already
        // tracking the entity. Do not mark every column modified: a stale
        // snapshot must be rejected by the xmin concurrency token instead of
        // overwriting a newer security change.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _unitOfWork.CommitAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException exception) when (attempt < MaxCounterRebaseAttempts)
            {
                // Retry only when the failure counter moved underneath us.
                if (!await TryRebaseOnLoginCounterChangeAsync(exception, ct))
                    throw;
            }
        }
    }

    private const int MaxCounterRebaseAttempts = 3;

    // Columns written by RegisterFailedLoginAsync/ResetLoginFailures. A
    // concurrent change limited to these does not make a password, email or
    // token-epoch update stale, so the save is retried on the fresh xmin.
    private static readonly HashSet<string> LoginCounterProperties =
    [
        nameof(User.FailedLoginAttempts),
        nameof(User.LockedUntil),
        nameof(User.UpdatedAt),
    ];

    private static async Task<bool> TryRebaseOnLoginCounterChangeAsync(
        DbUpdateConcurrencyException exception,
        CancellationToken ct)
    {
        foreach (var entry in exception.Entries)
        {
            if (entry.Entity is not User || entry.State != EntityState.Modified)
                return false;

            var databaseValues = await entry.GetDatabaseValuesAsync(ct);
            if (databaseValues is null)
                return false;

            foreach (var property in entry.Properties)
            {
                var name = property.Metadata.Name;
                var databaseValue = databaseValues[property.Metadata];
                if (property.Metadata.IsConcurrencyToken || Equals(property.OriginalValue, databaseValue))
                    continue;

                // Any other column changed concurrently: keep rejecting the
                // stale snapshot. Counter columns we modified ourselves are
                // also a real conflict (UpdatedAt is just a timestamp).
                if (!LoginCounterProperties.Contains(name)
                    || (property.IsModified && name != nameof(User.UpdatedAt)))
                    return false;
            }

            foreach (var property in entry.Properties)
            {
                var databaseValue = databaseValues[property.Metadata];
                if (property.Metadata.IsConcurrencyToken)
                {
                    // The WHERE clause uses the original token value.
                    property.OriginalValue = databaseValue;
                }
                else if (!property.IsModified)
                {
                    // Adopt the newer counter/lockout values without writing them.
                    property.OriginalValue = databaseValue;
                    property.CurrentValue = databaseValue;
                    property.IsModified = false;
                }
                else
                {
                    property.OriginalValue = databaseValue;
                    property.IsModified = true;
                }
            }
        }

        return true;
    }

    public async Task DeleteAsync(User user, CancellationToken ct = default)
    {
        Remove(user);
        await _unitOfWork.CommitAsync(ct);
    }

    public async Task<User?> RegisterFailedLoginAsync(
        Guid userId,
        int failureLimit,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failureLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockoutDuration, TimeSpan.Zero);

        now = now.ToUniversalTime();
        var lockoutUntil = now.Add(lockoutDuration);
        // Keep the counter and lockout decision in one SQL UPDATE. This
        // prevents concurrent password attempts from overwriting each other
        // after both requests loaded the same User entity.
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE nexusauth.users
            SET failed_login_attempts = CASE
                    WHEN locked_until IS NOT NULL AND locked_until <= {now}
                        THEN 1
                    WHEN failed_login_attempts + 1 >= {failureLimit}
                        THEN 0
                    ELSE failed_login_attempts + 1
                END,
                locked_until = CASE
                    WHEN locked_until IS NOT NULL AND locked_until <= {now}
                        AND 1 >= {failureLimit}
                        THEN {lockoutUntil}
                    WHEN (locked_until IS NULL OR locked_until <= {now})
                        AND failed_login_attempts + 1 >= {failureLimit}
                        THEN {lockoutUntil}
                    WHEN locked_until IS NOT NULL AND locked_until <= {now}
                        THEN NULL
                    ELSE locked_until
                END,
                updated_at = {now}
            WHERE id = {userId}
              AND is_active = true
              AND (locked_until IS NULL OR locked_until <= {now});
            """, ct);

        return await FindByIdAsync(userId, ct);
    }

    public async Task ResetLoginFailuresAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        await _dbContext.Set<User>()
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.FailedLoginAttempts, 0)
                    .SetProperty(u => u.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(u => u.UpdatedAt, now),
                ct);
    }

    public async Task<bool> TryResetLoginFailuresAsync(
        Guid userId,
        DateTimeOffset? expectedTokenInvalidBefore,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        // Compare the security epoch rather than xmin: a concurrent failed
        // guess bumps xmin too, and must not make a verified sign-in fail. A
        // lockout set by that guess is still honoured by the LockedUntil check.
        var affected = await _dbContext.Set<User>()
            .Where(user => user.Id == userId
                && user.IsActive
                && (user.LockedUntil == null || user.LockedUntil <= now)
                && user.TokenInvalidBefore == expectedTokenInvalidBefore)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.FailedLoginAttempts, 0)
                    .SetProperty(user => user.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(user => user.UpdatedAt, now),
                ct);
        return affected == 1;
    }

    private async Task<User?> ReloadUserAsync(Guid userId, CancellationToken ct)
    {
        var tracked = _dbContext.Set<User>().Local.FirstOrDefault(user => user.Id == userId);
        if (tracked is not null)
        {
            await _dbContext.Entry(tracked).ReloadAsync(ct);
            return _dbContext.Entry(tracked).State == EntityState.Detached ? null : tracked;
        }

        return await _dbContext.Set<User>().SingleOrDefaultAsync(user => user.Id == userId, ct);
    }

    // A short bounded wait lets a legitimate attempt that overlaps another
    // attempt on the same account (a double submit, a concurrent guess)
    // succeed instead of failing as "busy". Attempts stay serialized and the
    // wait is bounded, so a burst cannot hold connections indefinitely.
    private static readonly TimeSpan AuthenticationLockRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int AuthenticationLockAttempts = 10;

    private static async Task<bool> AcquireAuthenticationLockAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await TryAcquireAuthenticationLockAsync(connection, userId, ct))
                return true;
            if (attempt >= AuthenticationLockAttempts)
                return false;
            await Task.Delay(AuthenticationLockRetryDelay, ct);
        }
    }

    private static async Task<bool> TryAcquireAuthenticationLockAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = UserAuthenticationAttempt.TryLockSql;
        command.Parameters.AddWithValue("user_id", userId.ToString("D"));
        command.Parameters.AddWithValue("seed", UserAuthenticationAttempt.AdvisoryLockSeed);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

}
