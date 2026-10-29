using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchLoginLifecycleTests
{
    [Fact]
    public async Task Callback_requires_the_initiating_browser_and_cannot_be_replayed()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        Assert.Contains("httponly", login.Header.ToLowerInvariant());
        Assert.Contains("samesite=lax", login.Header.ToLowerInvariant());
        Assert.Contains("secure", login.Header.ToLowerInvariant());
        Assert.Contains("max-age=600", login.Header.ToLowerInvariant());
        Assert.DoesNotContain("verifier", login.Header);

        using var noCookie = await harness.Callback(login.State);
        Assert.Contains("invalid_callback", noCookie.Headers.Location!.ToString());
        using var wrongCookie = await harness.Callback(login.State, login.Cookie.Split('=')[0] + "=" + new string('0', 64));
        Assert.Contains("invalid_callback", wrongCookie.Headers.Location!.ToString());
        Assert.Equal(0, harness.Provider.Exchanges);

        using var legitimate = await harness.Callback(login.State, login.Cookie);
        Assert.Equal("https://dashboard.example.test/auth/callback", legitimate.Headers.Location!.ToString());
        Assert.Contains(legitimate.Headers.GetValues("Set-Cookie"), value => value.Contains(".NexusAuth.Workbench="));
        using var replay = await harness.Callback(login.State, login.Cookie);
        Assert.Contains("invalid_callback", replay.Headers.Location!.ToString());
        Assert.Equal(1, harness.Provider.Exchanges);
    }

    [Fact]
    public async Task Concurrent_callbacks_exchange_the_code_only_once()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => harness.Callback(login.State, login.Cookie)));
        try
        {
            Assert.Single(responses, response => response.Headers.Location!.AbsolutePath == "/auth/callback");
            Assert.Equal(1, harness.Provider.Exchanges);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Rejected_provider_response_also_requires_browser_binding_and_consumes_state()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        using var invalid = await harness.Callback(login.State, error: "access_denied");
        Assert.Contains("invalid_callback", invalid.Headers.Location!.ToString());
        using var rejected = await harness.Callback(login.State, login.Cookie, error: "access_denied");
        Assert.Equal("/oauth/error", rejected.Headers.Location!.AbsolutePath);
        using var replay = await harness.Callback(login.State, login.Cookie);
        Assert.Contains("invalid_callback", replay.Headers.Location!.ToString());
        Assert.Equal(0, harness.Provider.Exchanges);
    }

    [Fact]
    public async Task Full_state_store_rejects_login_before_discovery_and_failed_discovery_releases_capacity()
    {
        using var harness = new Harness(capacity: 1);
        harness.Provider.FailDiscovery = true;
        using var failed = await harness.Client.GetAsync("/api/auth/login");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        harness.Provider.FailDiscovery = false;
        _ = await harness.BeginLogin();
        var calls = harness.Provider.Discoveries;
        using var full = await harness.Client.GetAsync("/api/auth/login");
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(calls, harness.Provider.Discoveries);
    }

    [Fact]
    public async Task Anonymous_login_requests_are_rate_limited()
    {
        using var harness = new Harness();
        for (var i = 0; i < 20; i++)
        {
            using var response = await harness.Client.GetAsync("/api/auth/login");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var rejected = await harness.Client.GetAsync("/api/auth/login");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(20, harness.Provider.Discoveries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Logout_revokes_both_tokens_and_always_removes_the_local_cookie(bool failRevocation, bool localOnly)
    {
        using var harness = new Harness();
        harness.Provider.SignOutProvider = !localOnly;
        var login = await harness.BeginLogin();
        using var signedIn = await harness.Callback(login.State, login.Cookie);
        var sessionCookie = signedIn.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(".NexusAuth.Workbench=", StringComparison.Ordinal)).Split(';')[0];
        harness.Provider.FailRevocation = failRevocation;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", sessionCookie);
        using var loggedOut = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, loggedOut.StatusCode);
        Assert.Equal(new[] { "refresh_token:refresh", "access_token:access" }, harness.Provider.Revocations);
        Assert.Contains(loggedOut.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".NexusAuth.Workbench=;", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(await loggedOut.Content.ReadAsStringAsync());
        var url = body.RootElement.GetProperty("logoutUrl").GetString();
        if (localOnly) Assert.Equal(string.Empty, url);
        else
        {
            var endpoint = new Uri(url!);
            Assert.Equal("/custom/logout", endpoint.AbsolutePath);
            Assert.Equal("retained", QueryHelpers.ParseQuery(endpoint.Query)["existing"]);
        }
    }

    [Fact]
    public async Task Discovery_failure_during_logout_cannot_prevent_local_signout()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        using var signedIn = await harness.Callback(login.State, login.Cookie);
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".NexusAuth.Workbench=", StringComparison.Ordinal)).Split(';')[0];
        harness.Provider.FailDiscovery = true;
        harness.Provider.FailRevocation = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", cookie);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".NexusAuth.Workbench=;", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, body.RootElement.GetProperty("logoutUrl").GetString());
    }

    [Fact]
    public async Task State_expires_and_frees_capacity_without_consuming_another_browsers_flow()
    {
        var clock = new TestClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddNexusAuth(_ => { }).UseInMemory(options => options.Capacity = 1);
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IFlowStateStore>();
        await store.AddAsync("state", new FlowState("verifier", "nonce", "binding"));
        Assert.Null(await store.TryTakeAsync("state", "wrong"));
        await Assert.ThrowsAsync<FlowStateCapacityExceededException>(() => store.AddAsync("other", new FlowState("v", "n", "b")));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await store.TryTakeAsync("state", "binding"));
        await store.AddAsync("other", new FlowState("v", "n", "b"));
        Assert.NotNull(await store.TryTakeAsync("other", "b"));
        Assert.Null(await store.TryTakeAsync("other", "b"));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class Harness : IDisposable
    {
        private readonly WebApplicationFactory<WorkbenchApiModule> factory;
        public ProviderStub Provider { get; } = new();
        public HttpClient Client { get; }

        public Harness(int capacity = 10_000)
        {
            factory = new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing")
                    .UseSetting("Auth:Authority", "https://issuer.example.test")
                    .UseSetting("Auth:ClientId", "workbench")
                    .UseSetting("Auth:ClientSecret", "test-only-secret")
                    .UseSetting("Auth:RedirectUri", "https://dashboard.example.test/signin-oidc")
                    .UseSetting("Auth:PostLogoutRedirectUri", "https://dashboard.example.test/");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IOidcWorkbenchService>();
                    services.AddSingleton<IOidcWorkbenchService>(Provider);
                    services.Configure<WorkbenchFlowStateOptions>(options => options.Capacity = capacity);
                    services.PostConfigure<CookieAuthenticationOptions>(WorkbenchAuthenticationDefaults.CookieScheme, options =>
                    {
                        options.EventsType = null;
                        options.Events = new CookieAuthenticationEvents();
                    });
                });
            });
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://dashboard.example.test"), AllowAutoRedirect = false, HandleCookies = false,
            });
        }

        public async Task<(string State, string Cookie, string Header)> BeginLogin()
        {
            using var response = await Client.GetAsync("/api/auth/login");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var url = new Uri(body.RootElement.GetProperty("authorizeUrl").GetString()!);
            var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            return (QueryHelpers.ParseQuery(url.Query)["state"].ToString(), header.Split(';')[0], header);
        }

        public Task<HttpResponseMessage> Callback(string state, string? cookie = null, string? error = null)
        {
            var url = QueryHelpers.AddQueryString("/signin-oidc", new Dictionary<string, string?>
            {
                ["state"] = state, ["code"] = error is null ? "code" : null, ["error"] = error,
            });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            return Client.SendAsync(request);
        }

        public void Dispose() { Client.Dispose(); factory.Dispose(); }
    }

    private sealed class ProviderStub : IOidcWorkbenchService
    {
        public string Authority => "https://issuer.example.test";
        public string ClientId => "workbench";
        public string RedirectUri => "https://dashboard.example.test/signin-oidc";
        public string PostLogoutRedirectUri => "https://dashboard.example.test/";
        public string Scope => "openid profile offline_access";
        public bool SignOutProvider { get; set; } = true;
        public IFlowStateStore FlowStateStore => throw new NotSupportedException();
        public int Exchanges;
        public int Discoveries;
        public bool FailDiscovery;
        public bool FailRevocation;
        public List<string> Revocations { get; } = [];
        public string GenerateCodeVerifier() => "verifier";
        public (string codeChallenge, string codeVerifier) GeneratePkce() => ("challenge", "verifier");
        public Task<DiscoveryDocument> FetchDiscoveryAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Discoveries);
            if (FailDiscovery) throw new HttpRequestException("Provider unavailable");
            return Task.FromResult(new DiscoveryDocument
            {
                Issuer = Authority, AuthorizationEndpoint = Authority + "/authorize", EndSessionEndpoint = Authority + "/custom/logout?existing=retained",
            });
        }
        public Task<WorkbenchTokenResult> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct)
        {
            Interlocked.Increment(ref Exchanges);
            return Task.FromResult(new WorkbenchTokenResult("access", "refresh", "id", 120));
        }
        public Task<ValidatedIdToken> ValidateIdTokenAsync(DiscoveryDocument discovery, string idToken, string expectedNonce, CancellationToken ct) =>
            Task.FromResult(new ValidatedIdToken(Guid.NewGuid().ToString(), "Alice", "alice"));
        public Task<WorkbenchTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<AccessTokenIntrospectionResult> IntrospectAccessTokenAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct)
        {
            Revocations.Add(tokenTypeHint + ":" + token);
            if (FailRevocation) throw new HttpRequestException("Provider unavailable");
            return Task.CompletedTask;
        }
    }
}
