using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.Sessions;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Persistence.Repositories;
using NexusAuth.Shared.OAuth;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SecondFactorRecoveryPostgreSqlTests
{
    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Recovery_code_is_consumed_once_under_concurrent_requests(bool _)
    {
        var userId = await SeedUserAsync("recovery-concurrent");
        try
        {
            await using var generationContext = CreateContext();
            var generation = CreateService(generationContext);
            var codes = await generation.GenerateCodesAsync(userId);
            var code = codes[0];

            await using var firstContext = CreateContext();
            await using var secondContext = CreateContext();
            var results = await Task.WhenAll(
                CreateService(firstContext).RedeemAsync(userId, code),
                CreateService(secondContext).RedeemAsync(userId, code));

            Assert.Single(results, value => value);
            Assert.Single(results, value => !value);

            await using var readContext = CreateContext();
            Assert.Equal(1, await readContext.UserRecoveryCodes
                .CountAsync(item => item.UserId == userId && item.UsedAt != null));
        }
        finally
        {
            await DeleteUserGraphAsync(userId);
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Recovery_code_cannot_be_redeemed_for_another_user(bool _)
    {
        var firstUserId = await SeedUserAsync("recovery-owner");
        var secondUserId = await SeedUserAsync("recovery-other");
        try
        {
            await using var firstContext = CreateContext();
            var firstService = CreateService(firstContext);
            var codes = await firstService.GenerateCodesAsync(firstUserId);

            await using var secondContext = CreateContext();
            var secondService = CreateService(secondContext);
            Assert.False(await secondService.RedeemAsync(secondUserId, codes[0]));

            await using var readContext = CreateContext();
            Assert.True(await readContext.UserRecoveryCodes.AnyAsync(item =>
                item.UserId == firstUserId && item.UsedAt == null));
            Assert.True(await firstService.RedeemAsync(firstUserId, codes[0]));
        }
        finally
        {
            await DeleteUserGraphAsync(firstUserId);
            await DeleteUserGraphAsync(secondUserId);
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Reset_transaction_rolls_back_all_changes_when_audit_persistence_fails(bool _)
    {
        var userId = await SeedUserAsync("recovery-reset");
        Guid? clientEntityId = null;
        try
        {
            await using var setupContext = CreateContext();
            var setupUnitOfWork = new TestUnitOfWork(setupContext);
            var user = await setupContext.Users.SingleAsync(item => item.Id == userId);
            var now = DateTimeOffset.UtcNow;
            var passkey = WebAuthnCredential.Create(
                userId,
                [7, 8, 9],
                [10, 11, 12],
                0,
                Guid.NewGuid(),
                ["internal"],
                isBackupEligible: false,
                isBackedUp: false,
                now: now);
            var client = OAuthClient.Create(
                $"recovery-reset-{Guid.NewGuid():N}",
                "Recovery reset client");
            clientEntityId = client.Id;
            var refresh = RefreshToken.Create(client.ClientId, userId, OidcScopes.OpenId).Entity;
            var session = SsoSession.Create(userId, TimeSpan.FromHours(1));
            setupContext.AddRange(client, passkey, refresh, session);
            await setupContext.SaveChangesAsync();
            setupContext.ChangeTracker.Clear();

            var service = CreateService(setupContext);
            await service.GenerateCodesAsync(userId);
            setupContext.ChangeTracker.Clear();

            var failingRecovery = new FailingAuditRecoveryRepository(
                new SecondFactorRecoveryRepository(setupUnitOfWork));
            var failingService = CreateService(setupContext, failingRecovery);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failingService.ResetAsync(userId, Guid.NewGuid(), "故障注入"));
        }
        finally
        {
            try
            {
                await using var readContext = CreateContext();
                var storedUser = await readContext.Users.SingleAsync(item => item.Id == userId);
                Assert.Null(storedUser.TokenInvalidBefore);
                Assert.True(await readContext.UserCredentials.AnyAsync(item =>
                    item.UserId == userId && item.Type == UserCredential.TotpType && item.IsEnabled));
                Assert.True(await readContext.WebAuthnCredentials.AnyAsync(item =>
                    item.UserId == userId && item.DisabledAt == null));
                Assert.Equal(10, await readContext.UserRecoveryCodes.CountAsync(item => item.UserId == userId));
                Assert.True(await readContext.RefreshTokens.AnyAsync(item => item.UserId == userId && !item.IsRevoked));
                Assert.True(await readContext.SsoSessions.AnyAsync(item => item.UserId == userId && item.RevokedAt == null));
                Assert.False(await readContext.SecondFactorRecoveryAudits.AnyAsync(item => item.UserId == userId));
            }
            finally
            {
                await DeleteUserGraphAsync(userId, clientEntityId);
            }
        }
    }

    private static SecondFactorRecoveryService CreateService(
        AuthDbContext context,
        ISecondFactorRecoveryRepository? recovery = null)
    {
        var unitOfWork = new TestUnitOfWork(context);
        return new SecondFactorRecoveryService(
            new UserRepository(unitOfWork),
            new UserCredentialRepository(unitOfWork),
            new WebAuthnCredentialRepository(unitOfWork),
            recovery ?? new SecondFactorRecoveryRepository(unitOfWork),
            new RefreshTokenRepository(unitOfWork),
            new SsoSessionService(new SsoSessionRepository(unitOfWork)),
            Options.Create(new NexusAuthSecurityOptions()),
            NullLogger<SecondFactorRecoveryService>.Instance);
    }

    private static async Task<Guid> SeedUserAsync(string prefix)
    {
        await using var context = CreateContext();
        var user = User.Create($"{prefix}-{Guid.NewGuid():N}", "Test-password-only-123!", "Recovery test");
        var now = DateTimeOffset.UtcNow;
        var credential = UserCredential.CreatePendingTotp(user.Id, "test-protected-secret", now.AddMinutes(10), now: now);
        Assert.True(credential.ConfirmTotp("test-protected-secret", 0, now));
        context.Add(user);
        await context.SaveChangesAsync();
        context.Add(credential);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task DeleteUserGraphAsync(Guid userId, Guid? clientEntityId = null)
    {
        await using var context = CreateContext();
        await context.UserRecoveryCodes.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await context.SecondFactorRecoveryAudits.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await context.WebAuthnCredentials.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await context.UserCredentials.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await context.RefreshTokens.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await context.SsoSessions.Where(item => item.UserId == userId).ExecuteDeleteAsync();
        await context.Users.Where(item => item.Id == userId).ExecuteDeleteAsync();
        if (clientEntityId is { } id)
            await context.OAuthClients.Where(item => item.Id == id).ExecuteDeleteAsync();
    }

    private static AuthDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(Connection()).Options);

    private static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("An isolated PostgreSQL test database is required.");

        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not "localhost" and not "127.0.0.1"
            || settings.Database?.EndsWith("_utc_regression", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("MFA recovery tests require a local database ending in _utc_regression.");
        return connection;
    }

    private sealed class FailingAuditRecoveryRepository(ISecondFactorRecoveryRepository inner)
        : ISecondFactorRecoveryRepository
    {
        public Task ReplaceAsync(Guid userId, IReadOnlyCollection<UserRecoveryCode> codes, CancellationToken ct = default) =>
            inner.ReplaceAsync(userId, codes, ct);

        public Task<bool> TryRedeemAsync(Guid userId, string codeHash, DateTimeOffset now, CancellationToken ct = default) =>
            inner.TryRedeemAsync(userId, codeHash, now, ct);

        public Task DeleteForUserAsync(Guid userId, CancellationToken ct = default) =>
            inner.DeleteForUserAsync(userId, ct);

        public Task AddAuditAsync(SecondFactorRecoveryAudit audit, CancellationToken ct = default) =>
            throw new InvalidOperationException("audit failure");

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default) =>
            inner.ExecuteInTransactionAsync(operation, ct);
    }

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;

        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }
}
