using System.Net;
using System.Text;
using System.Text.Json;
using Luck.Framework.UnitOfWorks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Persistence.Repositories;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// Runs the new revocation/cleanup queries and the refresh reuse detection against PostgreSQL.
/// Requires NEXUSAUTH_TEST_POSTGRES_CONNECTION (an isolated *_utc_regression database).
/// </summary>
public sealed class OidcHardeningPostgreSqlTests
{
    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Client_cleanup_queries_only_touch_the_target_client(bool _)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var target = $"target-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";
        var user = Guid.NewGuid();
        var anotherUser = Guid.NewGuid();

        var targetTokens = new[] { RefreshToken.Create(target, user, "openid").Entity, RefreshToken.Create(target, user, "openid").Entity };
        var otherUserToken = RefreshToken.Create(target, anotherUser, "openid").Entity;
        var otherClientToken = RefreshToken.Create(other, user, "openid").Entity;
        var unusedCode = AuthorizationCode.Create(target, user, "https://client.example/cb", "openid").Entity;
        var otherCode = AuthorizationCode.Create(other, user, "https://client.example/cb", "openid").Entity;
        var pending = DeviceAuthorization.Create(target, "openid").Entity;
        var approved = DeviceAuthorization.Create(target, "openid").Entity;
        approved.Approve(user);
        var otherDevice = DeviceAuthorization.Create(other, "openid").Entity;
        var targetClientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        context.AddRange(targetTokens);
        context.AddRange(otherUserToken, otherClientToken, unusedCode, otherCode, pending, approved, otherDevice);
        context.AddRange(
            ClientApiResource.Create(targetClientId, Guid.NewGuid()),
            ClientApiResource.Create(targetClientId, Guid.NewGuid()),
            ClientApiResource.Create(otherClientId, Guid.NewGuid()));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(2, await new RefreshTokenRepository(unitOfWork).RevokeAllForUserAndClientAsync(user, target));
        Assert.Equal(1, await new AuthorizationCodeRepository(unitOfWork).InvalidateUnusedForClientAsync(target));
        Assert.Equal(2, await new DeviceAuthorizationRepository(unitOfWork).DenyOutstandingForClientAsync(target));
        await new ClientApiResourceRepository(unitOfWork).RemoveByClientIdAsync(targetClientId);

        var tokens = await context.Set<RefreshToken>().AsNoTracking()
            .Where(token => token.ClientId == target || token.ClientId == other).ToListAsync();
        Assert.All(tokens.Where(token => token.ClientId == target && token.UserId == user), token => Assert.True(token.IsRevoked));
        Assert.False(tokens.Single(token => token.Id == otherUserToken.Id).IsRevoked);
        Assert.False(tokens.Single(token => token.Id == otherClientToken.Id).IsRevoked);
        Assert.True((await context.Set<AuthorizationCode>().AsNoTracking().SingleAsync(code => code.Id == unusedCode.Id)).IsUsed);
        Assert.False((await context.Set<AuthorizationCode>().AsNoTracking().SingleAsync(code => code.Id == otherCode.Id)).IsUsed);
        var devices = await context.Set<DeviceAuthorization>().AsNoTracking()
            .Where(device => device.ClientId == target || device.ClientId == other).ToListAsync();
        Assert.All(devices.Where(device => device.ClientId == target), device => Assert.Equal(DeviceAuthorizationStatus.Denied, device.Status));
        Assert.Equal(DeviceAuthorizationStatus.Pending, devices.Single(device => device.Id == otherDevice.Id).Status);
        Assert.Equal(0, await context.Set<ClientApiResource>().CountAsync(association => association.ClientId == targetClientId));
        Assert.Equal(1, await context.Set<ClientApiResource>().CountAsync(association => association.ClientId == otherClientId));
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Replaying_a_rotated_refresh_token_revokes_its_descendant_in_the_real_host(bool _)
    {
        var clientId = $"miniapp-{Guid.NewGuid():N}";
        var appId = $"wx-{Guid.NewGuid():N}";
        using var factory = new WebApplicationFactory<AppWebModule>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseTestSetting("Smtp:Enabled", "false")
            .UseTestSetting("ConnectionStrings:Default", Connection())
            .UseTestSetting("WeChat:MiniProgram:Enabled", "true")
            .UseTestSetting("WeChat:MiniProgram:ClientId", clientId)
            .UseTestSetting("WeChat:MiniProgram:AppId", appId)
            .UseTestSetting("WeChat:MiniProgram:AppSecret", "test-secret")
            .ConfigureTestServices(services =>
            {
                var bootstrap = services.FirstOrDefault(value => value.ImplementationType == typeof(BootstrapAdminHostedService));
                if (bootstrap is not null) services.Remove(bootstrap);
                services.AddScoped(_ => CreateContext());
                services.AddScoped<IUnitOfWork>(provider => new TestUnitOfWork(provider.GetRequiredService<AuthDbContext>()));
                services.AddHttpClient(WeChatMiniProgramLoginService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StaticOpenIdHandler($"openid-{Guid.NewGuid():N}"));
            }));
        await using (var db = CreateContext())
        {
            db.Add(OAuthClient.Create(clientId, "Mini program", allowedScopes: ["openid", "profile", "offline_access"],
                allowedGrantTypes: [WeChatMiniProgramGrantTypes.GrantType, "refresh_token"],
                tokenEndpointAuthMethod: OAuthClient.TokenEndpointAuthMethodNone, requirePkce: false));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var login = await client.PostAsync("/connect/token", Form(
            ("grant_type", WeChatMiniProgramGrantTypes.GrantType), ("client_id", clientId), ("code", "code-1"), ("scope", "openid offline_access")));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var original = (await ReadAsync(login)).GetProperty("refresh_token").GetString()!;

        using var rotated = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", original)));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var descendant = (await ReadAsync(rotated)).GetProperty("refresh_token").GetString()!;

        await using (var db = CreateContext())
        {
            var parentHash = RefreshToken.Hash(original);
            var parent = await db.RefreshTokens.SingleAsync(token => token.TokenHash == parentHash);
            Set(db, parent, token => token.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-10));
            var marker = await db.TokenBlacklistEntries.SingleAsync(entry =>
                entry.Jti == RefreshToken.BuildRotationMarkerJti(parentHash));
            Set(db, marker, entry => entry.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-10));
            await db.SaveChangesAsync();

            await new AuthenticationDataCleanupRepository(new TestUnitOfWork(db)).CleanupAsync(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(-7),
                DateTimeOffset.UtcNow.AddDays(-180),
                batchSize: 100,
                maxBatchesPerType: 10);

            Assert.True(await db.RefreshTokens.AnyAsync(token => token.Id == parent.Id));
            Assert.True(await db.TokenBlacklistEntries.AnyAsync(entry => entry.Jti == marker.Jti));
        }

        using var replay = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", original)));
        Assert.Equal("invalid_grant", (await ReadAsync(replay)).GetProperty("error").GetString());

        using var afterReplay = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", descendant)));
        Assert.Equal("invalid_grant", (await ReadAsync(afterReplay)).GetProperty("error").GetString());
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Authentication_data_cleanup_deletes_only_rows_past_their_retention(bool _)
    {
        var statements = new List<string>();
        await using var context = CreateContext(statements.Add);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var unitOfWork = new TestUnitOfWork(context);
        var now = DateTimeOffset.UtcNow;
        var expired = now.AddDays(-10);
        var retained = now.AddDays(-1);
        var user = Guid.NewGuid();

        var expiredCode = AuthorizationCode.Create("cleanup-client", user, "https://client.example/cb", "openid").Entity;
        var retainedCode = AuthorizationCode.Create("cleanup-client", user, "https://client.example/cb", "openid").Entity;
        Set(context, expiredCode, value => value.ExpiresAt, expired);
        Set(context, retainedCode, value => value.ExpiresAt, retained);

        var expiredRefresh = RefreshToken.Create("cleanup-client", user, "openid").Entity;
        var retainedRefresh = RefreshToken.Create("cleanup-client", user, "openid").Entity;
        Set(context, expiredRefresh, value => value.ExpiresAt, expired);
        Set(context, retainedRefresh, value => value.ExpiresAt, retained);

        var expiredDevice = DeviceAuthorization.Create("cleanup-client", "openid").Entity;
        var retainedDevice = DeviceAuthorization.Create("cleanup-client", "openid").Entity;
        Set(context, expiredDevice, value => value.ExpiresAt, expired);
        Set(context, retainedDevice, value => value.ExpiresAt, retained);

        var expiredBlacklist = TokenBlacklistEntry.Create($"old-{Guid.NewGuid():N}", "access_token", user.ToString(), expired);
        var retainedBlacklist = TokenBlacklistEntry.Create($"new-{Guid.NewGuid():N}", "access_token", user.ToString(), retained);

        var expiredChallenge = WebAuthnChallenge.Create(
            $"old-{Guid.NewGuid():N}",
            WebAuthnChallenge.AuthenticationPurpose,
            null,
            "{}",
            null,
            false,
            now.AddMinutes(5));
        var retainedChallenge = WebAuthnChallenge.Create(
            $"new-{Guid.NewGuid():N}",
            WebAuthnChallenge.AuthenticationPurpose,
            null,
            "{}",
            null,
            false,
            now.AddMinutes(5));
        Set(context, expiredChallenge, value => value.ExpiresAt, expired);
        Set(context, retainedChallenge, value => value.ExpiresAt, retained);

        var revokedSession = SsoSession.Create(user, TimeSpan.FromDays(30));
        var retainedSession = SsoSession.Create(user, TimeSpan.FromDays(30));
        Set(context, revokedSession, value => value.RevokedAt, expired);
        Set(context, retainedSession, value => value.ExpiresAt, now.AddDays(1));

        var expiredAudit = LoginAuditLog.Create("expired", user, "cleanup-client", true, null, "127.0.0.1", "test");
        var retainedAudit = LoginAuditLog.Create("retained", user, "cleanup-client", true, null, "127.0.0.1", "test");
        Set(context, expiredAudit, value => value.OccurredAt, now.AddDays(-181));
        Set(context, retainedAudit, value => value.OccurredAt, now.AddDays(-179));

        context.AddRange(
            expiredCode,
            retainedCode,
            expiredRefresh,
            retainedRefresh,
            expiredDevice,
            retainedDevice,
            expiredBlacklist,
            retainedBlacklist,
            expiredChallenge,
            retainedChallenge,
            revokedSession,
            retainedSession,
            expiredAudit,
            retainedAudit);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await new AuthenticationDataCleanupRepository(unitOfWork).CleanupAsync(
            now,
            now.AddDays(-7),
            now.AddDays(-180),
            batchSize: 10,
            maxBatchesPerType: 10);

        Assert.True(result.AuthorizationCodes >= 1);
        Assert.True(result.RefreshTokens >= 1);
        Assert.True(result.DeviceAuthorizations >= 1);
        Assert.True(result.TokenBlacklistEntries >= 1);
        Assert.True(result.WebAuthnChallenges >= 1);
        Assert.True(result.SsoSessions >= 1);
        Assert.True(result.LoginAuditLogs >= 1);
        var deleteStatements = statements
            .Where(statement => statement.Contains("DELETE FROM", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(7, deleteStatements.Length);
        Assert.All(deleteStatements, statement => Assert.Contains("LIMIT", statement, StringComparison.Ordinal));
        Assert.False(await context.AuthorizationCodes.AnyAsync(value => value.Id == expiredCode.Id));
        Assert.True(await context.AuthorizationCodes.AnyAsync(value => value.Id == retainedCode.Id));
        Assert.False(await context.RefreshTokens.AnyAsync(value => value.Id == expiredRefresh.Id));
        Assert.True(await context.RefreshTokens.AnyAsync(value => value.Id == retainedRefresh.Id));
        Assert.False(await context.DeviceAuthorizations.AnyAsync(value => value.Id == expiredDevice.Id));
        Assert.True(await context.DeviceAuthorizations.AnyAsync(value => value.Id == retainedDevice.Id));
        Assert.False(await context.TokenBlacklistEntries.AnyAsync(value => value.Id == expiredBlacklist.Id));
        Assert.True(await context.TokenBlacklistEntries.AnyAsync(value => value.Id == retainedBlacklist.Id));
        Assert.False(await context.WebAuthnChallenges.AnyAsync(value => value.Id == expiredChallenge.Id));
        Assert.True(await context.WebAuthnChallenges.AnyAsync(value => value.Id == retainedChallenge.Id));
        Assert.False(await context.SsoSessions.AnyAsync(value => value.Id == revokedSession.Id));
        Assert.True(await context.SsoSessions.AnyAsync(value => value.Id == retainedSession.Id));
        Assert.False(await context.LoginAuditLogs.AnyAsync(value => value.Id == expiredAudit.Id));
        Assert.True(await context.LoginAuditLogs.AnyAsync(value => value.Id == retainedAudit.Id));
    }

    [PostgreSqlTheory]
    [Trait("Category", "PostgreSql")]
    [InlineData(true)]
    public async Task Device_authorization_try_add_recovers_from_a_user_code_unique_violation(bool _)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var repository = new DeviceAuthorizationRepository(new TestUnitOfWork(context));
        var first = DeviceAuthorization.Create("cleanup-client", "openid").Entity;
        context.Add(first);
        await context.SaveChangesAsync();

        var duplicate = DeviceAuthorization.Create("cleanup-client", "openid").Entity;
        Set(context, duplicate, value => value.UserCode, first.UserCode);
        Set(context, duplicate, value => value.UserCodeNormalized, first.UserCodeNormalized);

        Assert.False(await repository.TryAddAsync(duplicate));

        var replacement = DeviceAuthorization.Create("cleanup-client", "openid").Entity;
        Assert.True(await repository.TryAddAsync(replacement));
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static AuthDbContext CreateContext(Action<string>? log = null)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(Connection());
        if (log is not null)
            builder.LogTo(log, [RelationalEventId.CommandExecuting]);
        return new AuthDbContext(builder.Options);
    }

    private static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable)!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not "localhost" and not "127.0.0.1"
            || settings.Database?.EndsWith("_utc_regression", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("A local isolated _utc_regression database is required.");
        return connection;
    }

    private static void Set<TEntity, TProperty>(
        AuthDbContext context,
        TEntity entity,
        System.Linq.Expressions.Expression<Func<TEntity, TProperty>> property,
        TProperty value)
        where TEntity : class
    {
        context.Entry(entity).Property(property).CurrentValue = value;
    }

    private sealed class TestUnitOfWork(AuthDbContext context) : IUnitOfWork
    {
        public ILuckDbContext GetLuckDbContext() => context;
        public Task<int> CommitAsync(CancellationToken ct = default) => context.SaveChangesAsync(ct);
    }

    private sealed class StaticOpenIdHandler(string openId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { openid = openId, session_key = "server-only" }), Encoding.UTF8, "application/json"),
            });
    }
}
