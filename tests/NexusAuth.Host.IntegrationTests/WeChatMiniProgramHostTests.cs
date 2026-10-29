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

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Login_with_nickname_creates_and_updates_user_without_touching_other_users(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var maxNickname = new string('昵', 100);
        var initial = await LoginAndReadUserAsync(
            harness, client, "nickname-with-nickname", nickname: $"  {maxNickname}  ");
        var userId = Guid.Parse(initial.Sub);
        Assert.Equal(maxNickname, initial.Name);
        await AssertStoredNicknameAsync(userId, maxNickname);

        User firstBefore;
        User otherBefore;
        Guid otherUserId;
        await using (var db = CreateContext())
        {
            var first = await db.Users.SingleAsync(value => value.Id == userId);
            first.UpdateScimProfile(first.Username, first.Nickname, true,
                externalId: $"external-{Guid.NewGuid():N}",
                email: $"wechat-profile-{userId:N}@example.test",
                phoneNumber: $"+86{Random.Shared.NextInt64(13000000000, 19999999999)}",
                givenName: "Original",
                title: "WeChat user");
            var other = User.Create($"other-{Guid.NewGuid():N}", "test-password", "其他用户",
                email: $"other-{Guid.NewGuid():N}@example.test",
                phoneNumber: $"+86{Random.Shared.NextInt64(13000000000, 19999999999)}");
            otherUserId = other.Id;
            db.Users.Add(other);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            firstBefore = await db.Users.AsNoTracking().SingleAsync(value => value.Id == userId);
            otherBefore = await db.Users.AsNoTracking().SingleAsync(value => value.Id == otherUserId);
        }

        var updated = await LoginAndReadUserAsync(
            harness, client, "nickname-update", nickname: "  更新昵称  ");
        Assert.Equal(initial.Sub, updated.Sub);
        Assert.Equal("更新昵称", updated.Name);

        await using (var db = CreateContext())
        {
            var firstAfter = await db.Users.AsNoTracking().SingleAsync(value => value.Id == userId);
            var otherAfter = await db.Users.AsNoTracking().SingleAsync(value => value.Id == otherUserId);
            Assert.Equal("更新昵称", firstAfter.Nickname);
            Assert.True(firstAfter.UpdatedAt > firstBefore.UpdatedAt);
            Assert.Equal(firstBefore.Username, firstAfter.Username);
            Assert.Equal(firstBefore.PasswordHash, firstAfter.PasswordHash);
            Assert.Equal(firstBefore.Email, firstAfter.Email);
            Assert.Equal(firstBefore.PhoneNumber, firstAfter.PhoneNumber);
            Assert.Equal(firstBefore.ExternalId, firstAfter.ExternalId);
            Assert.Equal(firstBefore.GivenName, firstAfter.GivenName);
            Assert.Equal(firstBefore.Title, firstAfter.Title);
            Assert.Equal(firstBefore.IsActive, firstAfter.IsActive);
            Assert.Equal(firstBefore.IsSystemAccount, firstAfter.IsSystemAccount);

            Assert.Equal(otherBefore.Nickname, otherAfter.Nickname);
            Assert.Equal(otherBefore.Username, otherAfter.Username);
            Assert.Equal(otherBefore.PasswordHash, otherAfter.PasswordHash);
            Assert.Equal(otherBefore.Email, otherAfter.Email);
            Assert.Equal(otherBefore.PhoneNumber, otherAfter.PhoneNumber);
            Assert.Equal(otherBefore.IsActive, otherAfter.IsActive);
            Assert.Equal(otherBefore.UpdatedAt, otherAfter.UpdatedAt);
        }

        var omitted = await LoginAndReadUserAsync(
            harness, client, "nickname-omitted", nickname: null);
        Assert.Equal(initial.Sub, omitted.Sub);
        Assert.Equal("更新昵称", omitted.Name);
        await AssertStoredNicknameAsync(userId, "更新昵称");
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task First_login_without_nickname_uses_default_nickname(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var login = await LoginAndReadUserAsync(
            harness, client, "nickname-default", nickname: null);

        Assert.Equal("微信用户", login.Name);
        await AssertStoredNicknameAsync(Guid.Parse(login.Sub), "微信用户");
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Login_rejects_explicit_empty_blank_and_overlong_nicknames_before_upstream(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var invalidNicknames = new[]
        {
            "",
            "   ",
            $"  {new string('x', 101)}  ",
        };

        foreach (var nickname in invalidNicknames)
        {
            using var response = await harness.LoginAsync(
                client, $"invalid-nickname-{Guid.NewGuid():N}", nickname: nickname);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.Equal(0, harness.Upstream.Calls);
        await using var db = CreateContext();
        Assert.False(await db.UserExternalIdentities.AnyAsync(value => value.Issuer == harness.AppId));
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Login_accepts_100_emoji_nickname_and_rejects_101_before_upstream(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var acceptedNickname = string.Concat(Enumerable.Repeat("😀", 100));
        var login = await LoginAndReadUserAsync(
            harness, client, "nickname-100-emoji", nickname: acceptedNickname);

        Assert.Equal(acceptedNickname, login.Name);
        await AssertStoredNicknameAsync(Guid.Parse(login.Sub), acceptedNickname);
        Assert.Equal(1, harness.Upstream.Calls);

        var rejectedNickname = string.Concat(Enumerable.Repeat("😀", 101));
        using var response = await harness.LoginAsync(
            client, "nickname-101-emoji", nickname: rejectedNickname);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");

        Assert.Equal(1, harness.Upstream.Calls);
        await AssertStoredNicknameAsync(Guid.Parse(login.Sub), acceptedNickname);
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Login_with_nickname_requires_exact_profile_scope_before_upstream(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        using var missingProfile = await harness.LoginAsync(
            client, "nickname-missing-scope", scope: "openid", nickname: "不应写入");
        await AssertErrorAsync(missingProfile, HttpStatusCode.BadRequest, "invalid_scope");

        using var lookalikeProfile = await harness.LoginAsync(
            client, "nickname-lookalike-scope", scope: "openid profile.read",
            nickname: "不应写入");
        await AssertErrorAsync(lookalikeProfile, HttpStatusCode.BadRequest, "invalid_scope");

        Assert.Equal(0, harness.Upstream.Calls);
        await using var db = CreateContext();
        Assert.False(await db.UserExternalIdentities.AnyAsync(value => value.Issuer == harness.AppId));
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Invalid_wechat_code_does_not_change_user_nickname(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var initial = await LoginAndReadUserAsync(
            harness, client, "nickname-replayed-code", nickname: "原昵称");

        using var replay = await harness.LoginAsync(
            client, "nickname-replayed-code", nickname: "失败昵称");
        await AssertErrorAsync(replay, HttpStatusCode.BadRequest, "invalid_grant");

        Assert.Equal(2, harness.Upstream.Calls);
        await AssertStoredNicknameAsync(Guid.Parse(initial.Sub), "原昵称");
        await using var db = CreateContext();
        Assert.Equal(1, await db.UserExternalIdentities.CountAsync(value => value.Issuer == harness.AppId));
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task System_account_cannot_update_nickname_during_wechat_login(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var user = User.Create($"system-{Guid.NewGuid():N}", "test-password", "系统账号");
        user.MarkAsSystemAccount();
        await using (var db = CreateContext())
        {
            db.Users.Add(user);
            db.UserExternalIdentities.Add(UserExternalIdentity.Create(
                user.Id, "wechat_miniprogram", harness.AppId, harness.OpenId, null));
            await db.SaveChangesAsync();
        }

        using var response = await harness.LoginAsync(
            client, "nickname-system-account", nickname: "不应更新");
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_grant");
        Assert.Equal(1, harness.Upstream.Calls);
        await AssertStoredNicknameAsync(user.Id, "系统账号");
    }

    [PostgreSqlTheory]
    [InlineData(true)]
    public async Task Non_wechat_grant_ignores_nickname_and_does_not_update_user(bool _)
    {
        using var harness = await Harness.CreateAsync();
        using var client = harness.Factory.CreateClient();
        var initial = await LoginAndReadUserAsync(
            harness, client, "nickname-refresh-grant", nickname: "刷新前昵称");

        using var refreshed = await client.PostAsync("/connect/token", Form(
            ("grant_type", "refresh_token"),
            ("client_id", harness.ClientId),
            ("refresh_token", initial.RefreshToken),
            ("nickname", "不应写入")));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        await AssertStoredNicknameAsync(Guid.Parse(initial.Sub), "刷新前昵称");
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
    private static async Task<(string AccessToken, string RefreshToken, string Sub, string Name)> LoginAndReadUserAsync(
        Harness harness, HttpClient client, string code, string? nickname,
        string scopeName = "openid profile offline_access")
    {
        using var response = await harness.LoginAsync(client, code, scopeName, nickname);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("id_token").GetString());
        var user = body.GetProperty("user");
        Assert.Equal(idToken.Subject, user.GetProperty("sub").GetString());
        return (
            body.GetProperty("access_token").GetString()!,
            body.GetProperty("refresh_token").GetString()!,
            idToken.Subject,
            user.GetProperty("name").GetString()!);
    }
    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, (await ReadAsync(response)).GetProperty("error").GetString());
    }
    private static async Task AssertStoredNicknameAsync(Guid userId, string nickname)
    {
        await using var db = CreateContext();
        Assert.Equal(nickname, (await db.Users.AsNoTracking().SingleAsync(value => value.Id == userId)).Nickname);
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
                .UseTestSetting("Smtp:Enabled", "false")
                .UseTestSetting("ConnectionStrings:Default", Connection())
                .UseTestSetting("WeChat:MiniProgram:Enabled", "true")
                .UseTestSetting("WeChat:MiniProgram:ClientId", ClientId)
                .UseTestSetting("WeChat:MiniProgram:AppId", AppId)
                .UseTestSetting("WeChat:MiniProgram:AppSecret", "test-secret")
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
            db.Add(OAuthClient.Create(harness.ClientId, "测试小程序",
                allowedScopes: ["openid", "profile", "profile.read", "offline_access"],
                allowedGrantTypes: [WeChatMiniProgramGrantTypes.GrantType, "refresh_token"],
                tokenEndpointAuthMethod: "none", requirePkce: false));
            await db.SaveChangesAsync();
            return harness;
        }
        public Task<HttpResponseMessage> LoginAsync(
            HttpClient client, string code, string scope = "openid profile offline_access", string? nickname = null)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                new("grant_type", WeChatMiniProgramGrantTypes.GrantType),
                new("client_id", ClientId),
                new("code", code),
                new("scope", scope),
            };
            // Keep an explicitly supplied empty value so the host can distinguish it from omission.
            if (nickname is not null)
                values.Add(new("nickname", nickname));
            return client.PostAsync("/connect/token", new FormUrlEncodedContent(values));
        }
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
            return Task.FromResult(JsonResponse(content));
        }
        private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
            { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
