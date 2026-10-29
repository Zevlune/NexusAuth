using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Persistence.Repositories;
using NexusAuth.Shared.OAuth;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ConsentStorePostgreSqlTests
{
    [Fact]
    public void Consent_store_is_auto_registered_from_the_persistence_assembly()
    {
        var services = AutoDependencyServices.Create();

        var descriptor = Assert.Single(services, item => !item.IsKeyedService
            && item.ServiceType == typeof(IConsentStore)
            && item.ImplementationType == typeof(ConsentStore));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Request_approval_and_consumption_are_expiring_and_replay_safe(bool _)
    {
        await InstallUpgradeAsync();

        var userId = Guid.NewGuid();
        await using var setupContext = CreateContext();
        var store = CreateStore(setupContext);
        var requestId = await store.CreateRequestAsync(
            userId,
            "response_type=" + OAuthResponseTypes.Code + "&client_id=client&scope="
                + OidcScopes.OpenId + "%20" + OidcScopes.OfflineAccess,
            CancellationToken.None);

        var pending = await store.GetRequestAsync(requestId, userId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.False(pending.Remember);
        Assert.Null(await store.GetRequestAsync(requestId, Guid.NewGuid(), CancellationToken.None));

        await using var approveContext1 = CreateContext();
        await using var approveContext2 = CreateContext();
        var approvals = await Task.WhenAll(
            CreateStore(approveContext1).ApproveRequestAsync(
                requestId,
                userId,
                remember: true,
                CancellationToken.None),
            CreateStore(approveContext2).ApproveRequestAsync(
                requestId,
                userId,
                remember: true,
                CancellationToken.None));
        Assert.Single(approvals, approved => approved);

        await using var consumeContext1 = CreateContext();
        await using var consumeContext2 = CreateContext();
        var states = await Task.WhenAll(
            CreateStore(consumeContext1).ConsumeApprovedRequestAsync(
                requestId,
                userId,
                CancellationToken.None),
            CreateStore(consumeContext2).ConsumeApprovedRequestAsync(
                requestId,
                userId,
                CancellationToken.None));
        var consumed = Assert.Single(states, state => state is not null)!;
        Assert.Equal(pending.Query, consumed.Query);
        Assert.True(consumed.Remember);
        Assert.Null(await CreateStore(setupContext).ConsumeApprovedRequestAsync(
            requestId,
            userId,
            CancellationToken.None));

        var expiredId = await store.CreateRequestAsync(
            userId,
            "response_type=" + OAuthResponseTypes.Code + "&client_id=expired",
            CancellationToken.None);
        var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await setupContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE nexusauth.consent_requests
            SET created_at = {expiredAt.AddMinutes(-10)}, expires_at = {expiredAt}
            WHERE request_id_hash = {ConsentRequest.HashRequestId(expiredId)};
            """, CancellationToken.None);
        Assert.Null(await store.GetRequestAsync(
            expiredId,
            userId,
            CancellationToken.None));
        Assert.False(await store.ApproveRequestAsync(
            expiredId,
            userId,
            remember: false,
            CancellationToken.None));
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Grants_normalize_scope_and_match_claims_exactly_under_concurrent_writes(bool _)
    {
        await InstallUpgradeAsync();

        var userId = Guid.NewGuid();
        var clientId = $"client-{Guid.NewGuid():N}";
        const string claims = """{"id_token":{"email":{"essential":true}}}""";

        await using var writeContext1 = CreateContext();
        await using var writeContext2 = CreateContext();
        await Task.WhenAll(
            CreateStore(writeContext1).SaveGrantAsync(
                userId,
                clientId,
                OidcScopes.OpenId + " " + OidcScopes.Profile + " read",
                claims,
                CancellationToken.None),
            CreateStore(writeContext2).SaveGrantAsync(
                userId,
                clientId,
                "read " + OidcScopes.OpenId + " " + OidcScopes.Profile + " " + OidcScopes.OpenId,
                claims,
                CancellationToken.None));

        await using var readContext = CreateContext();
        var store = CreateStore(readContext);
        Assert.Equal(1, await readContext.Set<ConsentGrant>()
            .CountAsync(grant => grant.UserId == userId && grant.ClientId == clientId,
                CancellationToken.None));
        Assert.True(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.Profile + " read " + OidcScopes.OpenId,
            claims,
            CancellationToken.None));
        Assert.False(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.Profile + " read OpenID",
            claims,
            CancellationToken.None));
        Assert.False(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.Profile + " read " + OidcScopes.OpenId + " " + OidcScopes.OfflineAccess,
            claims,
            CancellationToken.None));
        Assert.False(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.Profile + " read " + OidcScopes.OpenId,
            claims + " ",
            CancellationToken.None));
        Assert.False(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.Profile + " read " + OidcScopes.OpenId,
            null,
            CancellationToken.None));

        await store.SaveGrantAsync(
            userId,
            clientId,
            OidcScopes.OpenId,
            null,
            CancellationToken.None);
        Assert.True(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.OpenId,
            null,
            CancellationToken.None));
        Assert.False(await store.HasGrantAsync(
            userId,
            clientId,
            OidcScopes.OpenId,
            string.Empty,
            CancellationToken.None));

        var grants = await store.ListGrantsAsync(userId, CancellationToken.None);
        Assert.Collection(
            grants,
            grant =>
            {
                Assert.Equal(clientId, grant.ClientId);
                Assert.Equal("openid", grant.Scope);
                Assert.Null(grant.Claims);
            },
            grant =>
            {
                Assert.Equal(clientId, grant.ClientId);
                Assert.Equal("openid profile read", grant.Scope);
                Assert.Equal(claims, grant.Claims);
            });

        Assert.Equal(0, await store.RevokeGrantsAsync(
            Guid.NewGuid(),
            clientId,
            CancellationToken.None));
        Assert.Equal(2, await store.RevokeGrantsAsync(
            userId,
            clientId,
            CancellationToken.None));
        Assert.Empty(await store.ListGrantsAsync(userId, CancellationToken.None));
        Assert.Equal(0, await store.RevokeGrantsAsync(
            userId,
            clientId,
            CancellationToken.None));
    }

    private static async Task InstallUpgradeAsync()
    {
        var scriptPath = FindUpgradeScript();
        var script = await File.ReadAllTextAsync(scriptPath, CancellationToken.None);
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        // Running twice proves the standalone upgrade can be reapplied.
        await ExecuteScriptAsync(connection, script);
        await ExecuteScriptAsync(connection, script);
    }

    private static async Task ExecuteScriptAsync(NpgsqlConnection connection, string script)
    {
        await using var command = new NpgsqlCommand(script, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string FindUpgradeScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "consent-store-upgrade.sql");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Could not locate consent-store-upgrade.sql.");
    }

    private static ConsentStore CreateStore(AuthDbContext context) =>
        new(new TestUnitOfWork(context));

    private static AuthDbContext CreateContext()
    {
        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(ConnectionString())
            .Options);
    }

    private static string ConnectionString()
    {
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("An isolated PostgreSQL test database is required.");

        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not "localhost" and not "127.0.0.1")
            throw new InvalidOperationException("Consent-store tests require a local PostgreSQL database.");

        return connection;
    }

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;

        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }
}
