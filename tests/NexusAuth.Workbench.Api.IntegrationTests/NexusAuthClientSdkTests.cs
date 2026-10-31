using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FrameworkOptions = Microsoft.Extensions.Options.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Extension;
using StackExchange.Redis;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

/// <summary>验证 SDK 脱离 Workbench 时的默认接入、PKCE、客户端认证与集群配置。</summary>
public sealed class NexusAuthClientSdkTests
{
    private const string Authority = "https://login.example.test";

    [Fact]
    public async Task Standalone_registration_uses_default_schemes_and_authorization_code_with_pkce()
    {
        using var provider = BuildProvider();

        var schemes = (await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync())
            .ToDictionary(scheme => scheme.Name, scheme => scheme.HandlerType);
        Assert.Equal(typeof(CookieAuthenticationHandler), schemes["NexusAuth.Cookie"]);
        Assert.Equal(typeof(OpenIdConnectHandler), schemes["NexusAuth.OpenIdConnect"]);
        Assert.Equal(typeof(JwtBearerHandler), schemes["Bearer"]);
        Assert.Equal("NexusAuth", provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value.DefaultScheme);

        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("NexusAuth.OpenIdConnect");
        Assert.True(oidc.UsePkce);
        Assert.Equal(OpenIdConnectResponseType.Code, oidc.ResponseType);
        Assert.Equal("/signin-oidc", oidc.CallbackPath.Value);
        Assert.Equal("NexusAuth.Cookie", oidc.SignInScheme);
        Assert.Equal(typeof(NexusAuthOpenIdConnectEvents), oidc.EventsType);
        Assert.Contains("api.test", oidc.Scope);

        var cookie = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("NexusAuth.Cookie");
        Assert.Equal(".NexusAuth.Session", cookie.Cookie.Name);
        Assert.Equal(TimeSpan.FromHours(24), cookie.ExpireTimeSpan);
        Assert.IsType<NexusAuthTokenRefreshCoordinator>(provider.GetRequiredService<INexusAuthTokenRefreshCoordinator>());
    }

    [Fact]
    public async Task Bearer_can_be_disabled_and_the_audience_becomes_optional()
    {
        using var provider = BuildProvider(new() { ["Auth:Audience"] = "" }, setup => setup.EnableBearer = false);

        provider.GetRequiredService<IStartupValidator>().Validate();
        var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.DoesNotContain(schemes, scheme => scheme.HandlerType == typeof(JwtBearerHandler));
    }

    [Fact]
    public void Registering_the_client_twice_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddNexusAuthClient(Configuration());

        Assert.Throws<InvalidOperationException>(() => services.AddNexusAuthClient(Configuration()));
    }

    [Theory]
    [InlineData(NexusAuthClientAuthentication.ClientSecretBasic)]
    [InlineData(NexusAuthClientAuthentication.ClientSecretPost)]
    public async Task Token_operations_use_the_configured_client_authentication(NexusAuthClientAuthentication mode)
    {
        var backchannel = new CapturingBackchannel();
        using var provider = BuildProvider(
            new() { ["Auth:ClientAuthentication"] = mode.ToString() },
            configureServices: services => UseStaticMetadata(services, backchannel));
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<INexusAuthTokenClient>()
            .RevokeTokenAsync("token-value", "refresh_token", CancellationToken.None);

        var (authorization, body) = Assert.Single(backchannel.Requests);
        var form = QueryHelpers.ParseQuery("?" + body);
        Assert.Equal("token-value", form["token"].ToString());
        if (mode == NexusAuthClientAuthentication.ClientSecretBasic)
        {
            Assert.StartsWith("Basic ", authorization);
            Assert.False(form.ContainsKey("client_secret"));
        }
        else
        {
            Assert.Null(authorization);
            Assert.Equal("sdk-client", form["client_id"].ToString());
            Assert.Equal("sdk-secret", form["client_secret"].ToString());
        }
    }

    [Theory]
    [InlineData("Auth:TokenRefresh:Provider", "Redis", "Auth:Redis:Configuration")]
    [InlineData("Auth:TokenRefresh:Provider", "redis", "Auth:TokenRefresh:Provider")]
    [InlineData("Auth:DataProtection:Provider", "Redis", "Auth:DataProtection:ApplicationName")]
    [InlineData("Auth:DataProtection:Provider", "FileSystem", "Auth:DataProtection:KeyPath")]
    [InlineData("Auth:Frontend:CallbackPath", "//evil.test", "Auth:Frontend:CallbackPath")]
    [InlineData("Auth:Session:CookieSecurePolicy", "None", "Auth:Session:CookieSecurePolicy")]
    public void Invalid_cluster_and_session_configuration_fails_startup(string key, string value, string expected)
    {
        using var provider = BuildProvider(new() { [key] = value });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Shared_redis_connection_backs_flow_state_and_the_data_protection_key_ring()
    {
        using var provider = BuildProvider(new()
        {
            ["Auth:Redis:Configuration"] = "localhost:1,abortConnect=false",
            ["Auth:FlowState:Provider"] = "Redis",
            ["Auth:TokenRefresh:Provider"] = "Redis",
            ["Auth:DataProtection:Provider"] = "Redis",
            ["Auth:DataProtection:ApplicationName"] = "sdk-app",
        });

        // Auth:Redis 已提供连接，流程状态无需再配置 FlowState:Redis:Configuration。
        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.Equal("RedisXmlRepository",
            provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository!.GetType().Name);
        Assert.Equal("sdk-app",
            provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Theory]
    [InlineData(null, "https://app.test/auth/callback")]
    [InlineData("/apps/42?tab=keys", "https://app.test/apps/42?tab=keys")]
    [InlineData("//evil.test/steal", "https://app.test/auth/callback")]
    [InlineData("/\\evil.test", "https://app.test/auth/callback")]
    [InlineData("https://evil.test/", "https://app.test/auth/callback")]
    public async Task Login_returns_a_pkce_authorize_url_and_only_accepts_local_return_urls(
        string? returnUrl, string expectedRedirect)
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        var path = "/api/auth/login" + (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl));
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var authorize = QueryHelpers.ParseQuery(new Uri(body!["authorizeUrl"]).Query);
        Assert.Equal("S256", authorize["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(authorize["code_challenge"]));
        Assert.Equal("https://app.test/signin-oidc", authorize["redirect_uri"].ToString());

        var oidc = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("NexusAuth.OpenIdConnect");
        var properties = oidc.StateDataFormat.Unprotect(authorize["state"]);
        Assert.Equal(expectedRedirect, properties!.RedirectUri);
        Assert.True(properties.Items.ContainsKey(OpenIdConnectFlowState.PropertyKey));
    }

    [Fact]
    public async Task Anonymous_logout_is_rejected_with_401_instead_of_a_login_redirect()
    {
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var logout = await client.PostAsync("/api/auth/logout", null);
        using var me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Redirect_mode_sends_the_browser_to_the_provider()
    {
        await using var app = await StartAppAsync(options => options.LoginResponse = NexusAuthLoginResponse.Redirect);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/auth/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(Authority + "/connect/authorize", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Cluster_nodes_exchange_a_rotating_refresh_token_only_once()
    {
        var redis = new FakeRedis();
        await AssertSingleExchangeAcrossNodes(redis.Connection);
    }

    [RedisFlowFact]
    public async Task Cluster_nodes_exchange_a_rotating_refresh_token_only_once_with_redis()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable(RedisFlowStateStoreTests.ConnectionVariable)!);
        await AssertSingleExchangeAcrossNodes(connection);
    }

    private static async Task AssertSingleExchangeAcrossNodes(IConnectionMultiplexer connection)
    {
        var exchanges = 0;
        var tokenClient = RecordingDispatchProxy.Create<INexusAuthTokenClient>((method, _) =>
        {
            if (method.Name != nameof(INexusAuthTokenClient.RefreshTokensAsync))
                throw new NotSupportedException(method.Name);
            Interlocked.Increment(ref exchanges);
            Thread.Sleep(200); // 保持持锁时间，让其他节点必须等待共享结果。
            return new NexusAuthTokenResult("rotated-access", "rotated-refresh", "id", 300);
        });
        // 两个节点各自拥有容器，只共享 Redis 与 Data Protection 密钥环。
        var keyRing = new EphemeralDataProtectionProvider();
        var options = FrameworkOptions.Create(new NexusAuthClientOptions
        {
            Redis = new NexusAuthRedisOptions { KeyPrefix = "NexusAuth:sdk-test:" + Guid.NewGuid().ToString("N") },
        });
        using var first = new ServiceCollection().AddScoped(_ => tokenClient).BuildServiceProvider();
        using var second = new ServiceCollection().AddScoped(_ => tokenClient).BuildServiceProvider();
        var nodes = new[]
        {
            new RedisTokenRefreshCoordinator(first.GetRequiredService<IServiceScopeFactory>(), connection, keyRing, options),
            new RedisTokenRefreshCoordinator(second.GetRequiredService<IServiceScopeFactory>(), connection, keyRing, options),
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => nodes[index % 2].RefreshAsync("old-refresh", CancellationToken.None))));

        Assert.Equal(1, exchanges);
        Assert.All(results, result => Assert.Equal("rotated-refresh", result.RefreshToken));
    }

    private static async Task<WebApplication> StartAppAsync(Action<NexusAuthEndpointOptions>? endpoints = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings());
        builder.Services.AddNexusAuthClient(builder.Configuration);
        builder.Services.AddAuthorization();
        UseStaticMetadata(builder.Services, new CapturingBackchannel());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNexusAuthEndpoints(configure: endpoints);
        await app.StartAsync();
        return app;
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?>? overrides = null,
        Action<NexusAuthClientSetup>? setup = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddNexusAuthClient(Configuration(overrides), setup);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void UseStaticMetadata(IServiceCollection services, CapturingBackchannel backchannel)
    {
        services.AddHttpClient("NexusAuth.Oidc").ConfigurePrimaryHttpMessageHandler(() => backchannel);
        services.PostConfigure<OpenIdConnectOptions>("NexusAuth.OpenIdConnect", options =>
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = Authority + "/connect/authorize",
                    TokenEndpoint = Authority + "/connect/token",
                    RevocationEndpoint = Authority + "/connect/revocation",
                    EndSessionEndpoint = Authority + "/connect/endsession",
                }));
        // 静态元数据不需要启动预热，避免后台服务访问不存在的 Provider。
        foreach (var descriptor in services.Where(descriptor =>
                     descriptor.ImplementationType?.Name == "MetadataWarmupService").ToArray())
            services.Remove(descriptor);
    }

    private static IConfigurationSection Configuration(Dictionary<string, string?>? overrides = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(Settings(overrides)).Build()
            .GetSection(NexusAuthClientOptions.SectionName);

    private static Dictionary<string, string?> Settings(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:Authority"] = Authority,
            ["Auth:Audience"] = "api.test",
            ["Auth:ClientId"] = "sdk-client",
            ["Auth:ClientSecret"] = "sdk-secret",
            ["Auth:RedirectUri"] = "https://app.test/signin-oidc",
            ["Auth:PostLogoutRedirectUri"] = "https://app.test/",
            ["Auth:Scope"] = "openid profile offline_access",
        };
        foreach (var (key, value) in overrides ?? []) values[key] = value;
        return values;
    }

    private sealed class CapturingBackchannel : HttpMessageHandler
    {
        public ConcurrentQueue<(string? Authorization, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue((request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "NexusAuth.Extension.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>只实现协调器用到的字符串与脚本命令，模拟多个节点共享的 Redis。</summary>
    private sealed class FakeRedis
    {
        private readonly ConcurrentDictionary<string, RedisValue> values = new(StringComparer.Ordinal);

        public FakeRedis()
        {
            var database = RecordingDispatchProxy.Create<IDatabase>((method, args) => method.Name switch
            {
                nameof(IDatabase.StringGetAsync) => values.TryGetValue(((RedisKey)args![0]!).ToString(), out var value)
                    ? value : RedisValue.Null,
                nameof(IDatabase.StringSetAsync) => Set((RedisKey)args![0]!, (RedisValue)args[1]!,
                    args.OfType<When>().FirstOrDefault()),
                nameof(IDatabase.ScriptEvaluateAsync) => Release(((RedisKey[])args![1]!)[0], ((RedisValue[])args[2]!)[0]),
                _ => throw new NotSupportedException(method.Name),
            });
            Connection = RecordingDispatchProxy.Create<IConnectionMultiplexer>((method, _) =>
                method.Name == nameof(IConnectionMultiplexer.GetDatabase)
                    ? database
                    : throw new NotSupportedException(method.Name));
        }

        public IConnectionMultiplexer Connection { get; }

        private bool Set(RedisKey key, RedisValue value, When when)
        {
            if (when == When.NotExists)
                return values.TryAdd(key!, value);
            values[key!] = value;
            return true;
        }

        private RedisResult Release(RedisKey key, RedisValue owner) =>
            RedisResult.Create(values.TryRemove(new KeyValuePair<string, RedisValue>(key!, owner)) ? 1 : 0);
    }
}
