using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Persistence.Repositories;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

public sealed class CredentialConcurrencyTests
{
    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(0)]
    public async Task Authentication_attempt_is_non_blocking_per_user(int _)
    {
        var id = await SeedUserAsync();
        try
        {
            await using var context = CreateContext();
            var users = new UserRepository(new TestUnitOfWork(context));

            var first = await users.BeginAuthenticationAttemptAsync(id);
            Assert.NotNull(first);

            await using (first!)
            {
                var busy = await users.BeginAuthenticationAttemptAsync(id);
                Assert.Null(busy);
            }

            var next = await users.BeginAuthenticationAttemptAsync(id);
            Assert.NotNull(next);
            await using (next!)
            {
            }
        }
        finally
        {
            await DeleteUserAsync(id);
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(0)]
    public async Task Stale_login_reset_cannot_clear_a_concurrent_failure(int _)
    {
        var id = await SeedUserAsync();
        try
        {
            await using var staleContext = CreateContext();
            var stale = await staleContext.Users.SingleAsync(user => user.Id == id);
            var expectedVersion = stale.Version;

            await using var concurrentContext = CreateContext();
            var concurrentUsers = new UserRepository(new TestUnitOfWork(concurrentContext));
            await concurrentUsers.RegisterFailedLoginAsync(
                id,
                failureLimit: 1,
                lockoutDuration: TimeSpan.FromMinutes(5),
                DateTimeOffset.UtcNow);

            var reset = await new UserRepository(new TestUnitOfWork(staleContext))
                .TryResetLoginFailuresAsync(id, expectedVersion, DateTimeOffset.UtcNow);
            Assert.False(reset);

            staleContext.ChangeTracker.Clear();
            var stored = await staleContext.Users.SingleAsync(user => user.Id == id);
            Assert.True(stored.IsLoginLocked(DateTimeOffset.UtcNow));
        }
        finally
        {
            await DeleteUserAsync(id);
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(0)]
    public async Task Stale_user_update_conflicts_after_a_concurrent_login_failure(int _)
    {
        var id = await SeedUserAsync();
        try
        {
            await using var staleContext = CreateContext();
            var stale = await staleContext.Users.SingleAsync(user => user.Id == id);

            await using var concurrentContext = CreateContext();
            await new UserRepository(new TestUnitOfWork(concurrentContext)).RegisterFailedLoginAsync(
                id,
                failureLimit: 3,
                lockoutDuration: TimeSpan.FromMinutes(5),
                DateTimeOffset.UtcNow);

            stale.UpdateNickname("stale update");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                new UserRepository(new TestUnitOfWork(staleContext)).UpdateAsync(stale));
        }
        finally
        {
            await DeleteUserAsync(id);
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData("scim")]
    [InlineData("open-api")]
    public async Task Stale_record_use_does_not_restore_a_concurrent_revocation(string kind)
    {
        var id = await SeedAsync(kind);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (kind == "scim")
            {
                await using var staleContext = CreateContext();
                var stale = await staleContext.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == id);
                Assert.True(stale.IsActive);
                await RevokeAsync(kind, id);
                await new ScimServicePrincipalCredentialRepository(new TestUnitOfWork(staleContext))
                    .RecordUseAsync(id, now);
            }
            else
            {
                await using var staleContext = CreateContext();
                var stale = await staleContext.OpenApiCredentials.SingleAsync(value => value.Id == id);
                Assert.True(stale.IsActive);
                await RevokeAsync(kind, id);
                await new OpenApiCredentialRepository(new TestUnitOfWork(staleContext))
                    .RecordUseAsync(id, now);
            }

            var stored = await ReadStateAsync(kind, id);
            Assert.False(stored.IsActive);
            Assert.NotNull(stored.RevokedAt);
            Assert.NotNull(stored.LastUsedAt);
            Assert.Equal(
                now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMicrosecond,
                stored.LastUsedAt!.Value.UtcTicks);
        }
        finally
        {
            await DeleteAsync(kind, id);
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData("scim")]
    [InlineData("open-api")]
    public async Task Stale_management_update_conflicts_after_a_concurrent_revocation(string kind)
    {
        var id = await SeedAsync(kind);
        try
        {
            if (kind == "scim")
            {
                await using var staleContext = CreateContext();
                var stale = await staleContext.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == id);
                await RevokeAsync(kind, id);

                stale.Update("stale-edit", [ScimServicePrincipalCredential.ScopeRead], null);
                stale.SetActive(true);
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                    new ScimServicePrincipalCredentialRepository(new TestUnitOfWork(staleContext)).UpdateAsync(stale));
            }
            else
            {
                await using var staleContext = CreateContext();
                var stale = await staleContext.OpenApiCredentials.SingleAsync(value => value.Id == id);
                await RevokeAsync(kind, id);

                stale.Update("stale-edit", null, isActive: true);
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                    new OpenApiCredentialRepository(new TestUnitOfWork(staleContext)).UpdateAsync(stale));
            }

            var stored = await ReadStateAsync(kind, id);
            Assert.False(stored.IsActive);
            Assert.NotNull(stored.RevokedAt);
            Assert.StartsWith("seed-", stored.Name);
        }
        finally
        {
            await DeleteAsync(kind, id);
        }
    }

    private static async Task<Guid> SeedAsync(string kind)
    {
        await using var context = CreateContext();
        var suffix = Guid.NewGuid().ToString("N");
        if (kind == "scim")
        {
            var credential = ScimServicePrincipalCredential.CreateWithToken(
                $"seed-{suffix}", $"scim-token-{suffix}", [ScimServicePrincipalCredential.ScopeRead]);
            context.Add(credential);
            await context.SaveChangesAsync();
            return credential.Id;
        }

        var openApi = OpenApiCredential.CreateWithToken(
            $"seed-{suffix}", $"open-api-token-{suffix}", OpenApiCredential.TargetTypeApplication);
        context.Add(openApi);
        await context.SaveChangesAsync();
        return openApi.Id;
    }

    private static async Task<Guid> SeedUserAsync()
    {
        await using var context = CreateContext();
        var user = User.Create(
            $"concurrency-{Guid.NewGuid():N}",
            "Test-password-only-123!",
            "Concurrency test");
        context.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task DeleteUserAsync(Guid id)
    {
        await using var context = CreateContext();
        await context.Users.Where(user => user.Id == id).ExecuteDeleteAsync();
    }

    private static async Task RevokeAsync(string kind, Guid id)
    {
        await using var context = CreateContext();
        if (kind == "scim")
        {
            var credential = await context.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == id);
            credential.Revoke();
        }
        else
        {
            var credential = await context.OpenApiCredentials.SingleAsync(value => value.Id == id);
            credential.Revoke();
        }
        await context.SaveChangesAsync();
    }

    private static async Task<CredentialState> ReadStateAsync(string kind, Guid id)
    {
        await using var context = CreateContext();
        if (kind == "scim")
        {
            var credential = await context.ScimServicePrincipalCredentials.SingleAsync(value => value.Id == id);
            return new CredentialState(credential.IsActive, credential.RevokedAt, credential.LastUsedAt, credential.Name);
        }

        var openApi = await context.OpenApiCredentials.SingleAsync(value => value.Id == id);
        return new CredentialState(openApi.IsActive, openApi.RevokedAt, openApi.LastUsedAt, openApi.Name);
    }

    private static async Task DeleteAsync(string kind, Guid id)
    {
        await using var context = CreateContext();
        if (kind == "scim")
            await context.ScimServicePrincipalCredentials.Where(value => value.Id == id).ExecuteDeleteAsync();
        else
            await context.OpenApiCredentials.Where(value => value.Id == id).ExecuteDeleteAsync();
    }

    private static AuthDbContext CreateContext()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable)
            ?? throw new InvalidOperationException($"Set {PostgreSqlTimestampTests.ConnectionVariable} to an isolated test database.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Host is not "localhost" and not "127.0.0.1"
            || settings.Database?.EndsWith("_utc_regression", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Credential concurrency regressions require a local database ending in _utc_regression.");
        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connectionString).Options);
    }

    private sealed record CredentialState(
        bool IsActive,
        DateTimeOffset? RevokedAt,
        DateTimeOffset? LastUsedAt,
        string Name);

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;
        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }
}
