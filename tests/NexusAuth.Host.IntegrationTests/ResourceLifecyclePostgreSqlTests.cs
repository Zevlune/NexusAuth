using Luck.Framework.UnitOfWorks;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Domain.AggregateRoots.ApiResources;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Persistence.Repositories;
using NexusAuth.Shared.OAuth;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// Exercises resource/client deletion against the real PostgreSQL constraints.
/// Requires NEXUSAUTH_TEST_POSTGRES_CONNECTION to point at a local *_utc_regression database.
/// </summary>
public sealed class ResourceLifecyclePostgreSqlTests
{
    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Deleting_a_resource_clears_every_client_audience_and_recreation_does_not_restore_it(bool _)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var audience = $"resource-{suffix}.api";
        var otherAudience = $"other-{suffix}.api";
        var resource = ApiResource.Create($"resource-{suffix}", "Resource", audience);
        var assignedClient = OAuthClient.Create(
            $"assigned-{suffix}",
            "Assigned client",
            allowedScopes: [OidcScopes.OpenId, audience]);
        var legacyClient = OAuthClient.Create(
            $"legacy-{suffix}",
            "Legacy client",
            allowedScopes: [audience]);
        var unrelatedClient = OAuthClient.Create(
            $"unrelated-{suffix}",
            "Unrelated client",
            allowedScopes: [otherAudience]);

        context.AddRange(resource, assignedClient, legacyClient, unrelatedClient);
        context.Add(ClientApiResource.Create(assignedClient.Id, resource.Id));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var resourceRepository = new ApiResourceRepository(new TestUnitOfWork(context));
        var persistedResource = await context.ApiResources.SingleAsync(value => value.Id == resource.Id);
        await resourceRepository.DeleteAsync(persistedResource);
        context.ChangeTracker.Clear();

        Assert.False(await context.ApiResources.AnyAsync(value => value.Id == resource.Id));
        Assert.False(await context.ClientApiResources.AnyAsync(value => value.ApiResourceId == resource.Id));

        var scopesAfterDelete = await context.OAuthClients
            .AsNoTracking()
            .Where(value => value.Id == assignedClient.Id
                || value.Id == legacyClient.Id
                || value.Id == unrelatedClient.Id)
            .ToDictionaryAsync(value => value.Id, value => value.AllowedScopes);
        Assert.DoesNotContain(audience, scopesAfterDelete[assignedClient.Id]);
        Assert.DoesNotContain(audience, scopesAfterDelete[legacyClient.Id]);
        Assert.Equal([otherAudience], scopesAfterDelete[unrelatedClient.Id]);

        // Reusing an audience after deletion must not inherit an old client's authorization.
        var replacement = ApiResource.Create($"replacement-{suffix}", "Replacement", audience);
        context.Add(replacement);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var scopesAfterRecreate = (await context.OAuthClients
            .AsNoTracking()
            .Where(value => value.Id == assignedClient.Id
                || value.Id == legacyClient.Id
                || value.Id == unrelatedClient.Id)
            .Select(value => value.AllowedScopes)
            .ToListAsync())
            .SelectMany(value => value)
            .ToList();
        Assert.DoesNotContain(audience, scopesAfterRecreate);
        Assert.False(await context.ClientApiResources.AnyAsync(value => value.ApiResourceId == replacement.Id));
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task A_stale_client_snapshot_cannot_restore_audience_after_resource_deletion(bool _)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var audience = $"stale-{suffix}.api";
        var resource = ApiResource.Create($"stale-resource-{suffix}", "Stale resource", audience);
        var client = OAuthClient.Create(
            $"stale-client-{suffix}",
            "Stale client",
            allowedScopes: [audience]);

        await using (var setupContext = CreateContext())
        {
            setupContext.AddRange(resource, client);
            setupContext.Add(ClientApiResource.Create(client.Id, resource.Id));
            await setupContext.SaveChangesAsync();
        }

        try
        {
            await using var staleContext = CreateContext();
            var staleClient = await staleContext.OAuthClients
                .SingleAsync(value => value.Id == client.Id);

            await using (var deleteContext = CreateContext())
            {
                var resourceToDelete = await deleteContext.ApiResources
                    .SingleAsync(value => value.Id == resource.Id);
                await new ApiResourceRepository(new TestUnitOfWork(deleteContext))
                    .DeleteAsync(resourceToDelete);
            }

            staleClient.Update(clientName: "stale overwrite");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                new OAuthClientRepository(new TestUnitOfWork(staleContext)).UpdateAsync(staleClient));

            await using var readContext = CreateContext();
            var storedClient = await readContext.OAuthClients
                .AsNoTracking()
                .SingleAsync(value => value.Id == client.Id);
            Assert.DoesNotContain(audience, storedClient.AllowedScopes);
            Assert.False(await readContext.ApiResources.AnyAsync(value => value.Id == resource.Id));
        }
        finally
        {
            await using var cleanupContext = CreateContext();
            await cleanupContext.ClientApiResources
                .Where(value => value.ClientId == client.Id || value.ApiResourceId == resource.Id)
                .ExecuteDeleteAsync();
            await cleanupContext.ApiResources
                .Where(value => value.Id == resource.Id)
                .ExecuteDeleteAsync();
            await cleanupContext.OAuthClients
                .Where(value => value.Id == client.Id)
                .ExecuteDeleteAsync();
        }
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Deleting_a_client_cleans_associations_secrets_and_token_rows(bool _)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var client = OAuthClient.Create($"deleted-client-{suffix}", "Deleted client");
        var resource = ApiResource.Create($"client-resource-{suffix}", "Client resource", $"client-{suffix}.api");
        var secret = OAuthClientSecret.CreateSharedSecret(client.Id, $"secret-{suffix}");
        var userId = Guid.NewGuid();
        var authorizationCode = AuthorizationCode.Create(
            client.ClientId,
            userId,
            "https://client.example.test/callback",
            OidcScopes.OpenId).Entity;
        var refreshToken = RefreshToken.Create(client.ClientId, userId, OidcScopes.OpenId).Entity;
        var pendingDevice = DeviceAuthorization.Create(client.ClientId, OidcScopes.OpenId).Entity;
        var approvedDevice = DeviceAuthorization.Create(client.ClientId, OidcScopes.OpenId).Entity;
        approvedDevice.Approve(userId);
        var consent = ConsentGrant.Create(userId, client.ClientId, OidcScopes.OpenId, claims: null);

        context.AddRange(client, resource, secret, authorizationCode, refreshToken, pendingDevice, approvedDevice, consent);
        context.Add(ClientApiResource.Create(client.Id, resource.Id));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var repository = new OAuthClientRepository(new TestUnitOfWork(context));
        var persistedClient = await repository.GetByIdAsync(client.Id);
        Assert.NotNull(persistedClient);
        await repository.DeleteAsync(persistedClient!);
        context.ChangeTracker.Clear();

        Assert.False(await context.OAuthClients.AnyAsync(value => value.Id == client.Id));
        Assert.False(await context.OAuthClientSecrets.AnyAsync(value => value.ClientId == client.Id));
        Assert.False(await context.ClientApiResources.AnyAsync(value => value.ClientId == client.Id));
        Assert.False(await context.AuthorizationCodes.AnyAsync(value => value.ClientId == client.ClientId));
        Assert.False(await context.RefreshTokens.AnyAsync(value => value.ClientId == client.ClientId));
        Assert.False(await context.Set<ConsentGrant>().AnyAsync(value => value.ClientId == client.ClientId));

        var deviceStates = await context.DeviceAuthorizations
            .AsNoTracking()
            .Where(value => value.ClientId == client.ClientId)
            .Select(value => value.Status)
            .ToListAsync();
        Assert.Equal(
            [DeviceAuthorizationStatus.Denied, DeviceAuthorizationStatus.Denied],
            deviceStates.OrderBy(value => value).ToArray());
        Assert.True(await context.ApiResources.AnyAsync(value => value.Id == resource.Id));
    }

    private static AuthDbContext CreateContext()
    {
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable)
            ?? throw new InvalidOperationException(
                $"Set {PostgreSqlTimestampTests.ConnectionVariable} to an isolated test database.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not "localhost" and not "127.0.0.1"
            || settings.Database?.EndsWith("_utc_regression", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                "Resource lifecycle regressions require a local database ending in _utc_regression.");
        }

        return new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connection)
            .Options);
    }

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;

        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }
}
