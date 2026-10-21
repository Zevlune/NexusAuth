using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
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
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Persistence.Repositories;
using Npgsql;
using Xunit;
using AuthDbContext = NexusAuth.Persistence.DbContext;

namespace NexusAuth.Host.IntegrationTests;

public sealed class WeChatMiniProgramHostTests
{
    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Login_userinfo_refresh_replay_and_disabled_user_use_real_host_and_database(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        using var firstResponse = await harness.LoginAsync(client, "first");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await ReadAsync(firstResponse);
        var access = first.GetProperty("access_token").GetString()!;
        var refresh = first.GetProperty("refresh_token").GetString()!;
        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(first.GetProperty("id_token").GetString());
        var sub = idToken.Subject;
        Assert.NotEqual(harness.OpenId, sub);
        Assert.Contains(idToken.Claims, value => value.Type == "amr" && value.Value == "wechat");
        Assert.DoesNotContain("session_key", first.GetRawText());
        Assert.False(firstResponse.Headers.Contains("Set-Cookie"));

        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        using var infoResponse = await client.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode);
        Assert.Equal(sub, (await ReadAsync(infoResponse)).GetProperty("sub").GetString());
        client.DefaultRequestHeaders.Authorization = null;

        using var secondResponse = await harness.LoginAsync(client, "second");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(sub, new JwtSecurityTokenHandler().ReadJwtToken(
            (await ReadAsync(secondResponse)).GetProperty("id_token").GetString()).Subject);
        using var replay = await harness.LoginAsync(client, "first");
        Assert.Equal("invalid_grant", (await ReadAsync(replay)).GetProperty("error").GetString());

        using var rotated = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", harness.ClientId), ("refresh_token", refresh)));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var replacement = (await ReadAsync(rotated)).GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refresh, replacement);
        using var reused = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", harness.ClientId), ("refresh_token", refresh)));
        Assert.Equal("invalid_grant", (await ReadAsync(reused)).GetProperty("error").GetString());

        await using (var db = CreateContext())
        {
            Assert.Equal(1, await db.UserExternalIdentities.CountAsync(value => value.Issuer == harness.AppId));
            var user = await db.Users.SingleAsync(value => value.Id == Guid.Parse(sub));
            user.UpdateScimProfile(user.Username, user.Nickname, false);
            await db.SaveChangesAsync();
        }
        using var disabled = await harness.LoginAsync(client, "disabled");
        Assert.Equal("invalid_grant", (await ReadAsync(disabled)).GetProperty("error").GetString());
        using var disabledRefresh = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"), ("client_id", harness.ClientId), ("refresh_token", replacement)));
        Assert.Equal("invalid_grant", (await ReadAsync(disabledRefresh)).GetProperty("error").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/connect/userinfo")).StatusCode);
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Public_client_is_limited_to_configured_grants_and_upstream_is_not_called_for_bad_requests(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        using var wrongClient = await client.PostAsync("/connect/token", Form(
            ("grant_type", WeChatMiniProgramGrantTypes.GrantType), ("client_id", "another-client"),
            ("code", "test"), ("scope", "openid")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongClient.StatusCode);
        using var secret = await client.PostAsync("/connect/token", Form(
            ("grant_type", WeChatMiniProgramGrantTypes.GrantType), ("client_id", harness.ClientId),
            ("client_secret", "must-not-be-used"), ("code", "test"), ("scope", "openid")));
        Assert.Equal(HttpStatusCode.Unauthorized, secret.StatusCode);
        using var scope = await harness.LoginAsync(client, "test", "unregistered-scope");
        Assert.Equal("invalid_scope", (await ReadAsync(scope)).GetProperty("error").GetString());
        using var missingCode = await harness.LoginAsync(client, "");
        Assert.Equal("invalid_request", (await ReadAsync(missingCode)).GetProperty("error").GetString());
        using var credentials = await client.PostAsync("/connect/token", Form(
            ("grant_type", "client_credentials"), ("client_id", harness.ClientId), ("scope", "openid")));
        Assert.Equal(HttpStatusCode.Unauthorized, credentials.StatusCode);
        Assert.Equal(0, harness.Upstream.Calls);
        using var unavailable = await harness.LoginAsync(client, "unavailable");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("temporarily_unavailable", (await ReadAsync(unavailable)).GetProperty("error").GetString());
        using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
        Assert.Contains(discovery.RootElement.GetProperty("grant_types_supported").EnumerateArray(),
            value => value.GetString() == WeChatMiniProgramGrantTypes.GrantType);
        Assert.Contains(discovery.RootElement.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray(),
            value => value.GetString() == "none");
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Concurrent_registration_rolls_back_losing_users_and_preserves_app_isolation(bool _)
    {
        var issuer = $"concurrent-{Guid.NewGuid():N}";
        var candidates = Enumerable.Range(0, 4).Select(_ => User.Create($"wx_{Guid.NewGuid():N}", "test-password", "测试")).ToArray();
        var ids = candidates.Select(value => value.Id).ToArray();
        var results = await Task.WhenAll(candidates.Select(async user =>
        {
            await using var context = CreateContext();
            var repository = new UserExternalIdentityRepository(new TestUnitOfWork(context));
            return await repository.GetOrCreateUserAsync(user,
                UserExternalIdentity.Create(user.Id, "wechat_miniprogram", issuer, "same-openid", "same-unionid"));
        }));
        Assert.Single(results.Select(value => value.Id).Distinct());
        await using var db = CreateContext();
        Assert.Equal(1, await db.Users.CountAsync(value => ids.Contains(value.Id)));
        Assert.Equal(1, await db.UserExternalIdentities.CountAsync(value => value.Issuer == issuer));
        var other = User.Create($"wx_{Guid.NewGuid():N}", "test-password", "另一个App");
        var otherUser = await new UserExternalIdentityRepository(new TestUnitOfWork(db)).GetOrCreateUserAsync(other,
            UserExternalIdentity.Create(other.Id, "wechat_miniprogram", issuer + "-other", "same-openid", "same-unionid"));
        Assert.NotEqual(results[0].Id, otherUser.Id);
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
    private sealed class Harness : IDisposable
    {
        public string ClientId { get; } = $"miniapp-{Guid.NewGuid():N}";
        public string AppId { get; } = $"wx-{Guid.NewGuid():N}";
        public string OpenId { get; } = $"openid-{Guid.NewGuid():N}";
        public UpstreamHandler Upstream { get; }
        public WebApplicationFactory<AppWebModule> Factory { get; }
        private Harness()
        {
            Upstream = new(OpenId);
            Factory = new WebApplicationFactory<AppWebModule>().WithWebHostBuilder(builder => builder
                .UseEnvironment("Development")
                .UseSetting("Smtp:Enabled", "false")
                .UseSetting("ConnectionStrings:Default", Connection())
                .UseSetting("WeChat:MiniProgram:Enabled", "true")
                .UseSetting("WeChat:MiniProgram:ClientId", ClientId)
                .UseSetting("WeChat:MiniProgram:AppId", AppId)
                .UseSetting("WeChat:MiniProgram:AppSecret", "test-secret")
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<BootstrapAdminHostedService>();
                    var bootstrap = services.FirstOrDefault(value => value.ImplementationType == typeof(BootstrapAdminHostedService));
                    if (bootstrap is not null) services.Remove(bootstrap);
                    services.AddScoped(_ => CreateContext());
                    services.AddScoped<IUnitOfWork>(provider => new TestUnitOfWork(provider.GetRequiredService<AuthDbContext>()));
                    services.AddHttpClient(WeChatMiniProgramLoginService.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => Upstream);
                }));
        }
        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            await using var db = CreateContext();
            db.Add(OAuthClient.Create(harness.ClientId, "测试小程序", allowedScopes: ["openid", "profile", "offline_access"],
                allowedGrantTypes: [WeChatMiniProgramGrantTypes.GrantType, "refresh_token"],
                tokenEndpointAuthMethod: "none", requirePkce: false));
            await db.SaveChangesAsync();
            return harness;
        }
        public Task<HttpResponseMessage> LoginAsync(HttpClient client, string code, string scope = "openid profile offline_access") =>
            client.PostAsync("/connect/token", Form(("grant_type", WeChatMiniProgramGrantTypes.GrantType),
                ("client_id", ClientId), ("code", code), ("scope", scope)));
        public void Dispose() { Factory.Dispose(); Upstream.Dispose(); }
    }
    private sealed class UpstreamHandler(string openId) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte> consumed = new();
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            var code = query["js_code"].ToString();
            var content = code == "unavailable" ? "{\"errcode\":-1}"
                : consumed.TryAdd(code, 0) ? JsonSerializer.Serialize(new { openid = openId, session_key = "server-only-session" })
                : "{\"errcode\":40163}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }
}
