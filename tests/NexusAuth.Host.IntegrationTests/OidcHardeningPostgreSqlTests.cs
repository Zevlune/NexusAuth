using System.Net;
using System.Text;
using System.Text.Json;
using Luck.Framework.UnitOfWorks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Domain.AggregateRoots.OAuthClients;
using NexusAuth.Domain.Entities;
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
            .UseSetting("Smtp:Enabled", "false")
            .UseSetting("ConnectionStrings:Default", Connection())
            .UseSetting("WeChat:MiniProgram:Enabled", "true")
            .UseSetting("WeChat:MiniProgram:ClientId", clientId)
            .UseSetting("WeChat:MiniProgram:AppId", appId)
            .UseSetting("WeChat:MiniProgram:AppSecret", "test-secret")
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

        using var replay = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", original)));
        Assert.Equal("invalid_grant", (await ReadAsync(replay)).GetProperty("error").GetString());

        using var afterReplay = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", descendant)));
        Assert.Equal("invalid_grant", (await ReadAsync(afterReplay)).GetProperty("error").GetString());
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static AuthDbContext CreateContext() => new(new DbContextOptionsBuilder<AuthDbContext>()
        .UseNpgsql(Connection()).Options);

    private static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTimestampTests.ConnectionVariable)!;
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Host is not "localhost" and not "127.0.0.1"
            || settings.Database?.EndsWith("_utc_regression", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("A local isolated _utc_regression database is required.");
        return connection;
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
