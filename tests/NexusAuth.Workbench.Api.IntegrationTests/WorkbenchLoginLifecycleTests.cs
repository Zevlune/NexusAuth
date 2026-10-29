using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchLoginLifecycleTests
{
    private const string Authority = "https://issuer.example.test";
    private const string Audience = "nexusauth.workbench.api";
    private const string ClientId = "workbench";
    private const string RedirectUri = "https://dashboard.example.test/signin-oidc";
    private const string PostLogoutRedirectUri = "https://dashboard.example.test/";
    private const string WorkbenchBindingCookiePrefix = "__Host-NexusAuth.Oidc.";
    private const string CorrelationCookiePrefix = ".AspNetCore.Correlation.";
    private const string NonceCookiePrefix = ".AspNetCore.OpenIdConnect.Nonce.";

    [Fact]
    public async Task Begin_login_returns_bound_native_cookies_and_pkce_s256()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        var query = QueryHelpers.ParseQuery(login.AuthorizeUrl.Query);

        Assert.Equal(3, login.SetCookieHeaders.Count);
        Assert.Contains("max-age=600", login.BindingCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal(login.Nonce, query["nonce"].ToString());
        Assert.Equal(RedirectUri, query["redirect_uri"].ToString());

        using var callback = await harness.Callback(login);
        AssertCallbackSucceeded(callback);

        var exchange = Assert.Single(harness.Provider.Exchanges);
        var expectedChallenge = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.UTF8.GetBytes(exchange.CodeVerifier)));
        Assert.Equal(query["code_challenge"].ToString(), expectedChallenge);
        Assert.Equal(RedirectUri, exchange.RedirectUri);
    }

    [Fact]
    public async Task Callback_requires_the_initiating_browser_and_cannot_be_replayed()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();

        using var noCookie = await harness.Callback(login.State);
        AssertCallbackRejected(noCookie, "invalid_callback");
        Assert.Equal(0, harness.Provider.ExchangeCount);

        var wrongBinding = new Dictionary<string, string>(login.Cookies, StringComparer.Ordinal);
        var bindingCookieName = Assert.Single(
            wrongBinding.Keys,
            name => name.StartsWith(WorkbenchBindingCookiePrefix, StringComparison.Ordinal));
        wrongBinding[bindingCookieName] = new string('0', 64);
        using var wrongCookie = await harness.Callback(login.State, wrongBinding);
        AssertCallbackRejected(wrongCookie, "invalid_callback");
        Assert.Equal(0, harness.Provider.ExchangeCount);

        using var legitimate = await harness.Callback(login);
        AssertCallbackSucceeded(legitimate);
        using var replay = await harness.Callback(login);
        AssertCallbackRejected(replay, "invalid_callback");
        Assert.Equal(1, harness.Provider.ExchangeCount);
    }

    [Fact]
    public async Task Concurrent_callbacks_exchange_the_code_only_once()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => harness.Callback(login)));
        try
        {
            Assert.Single(responses, IsSuccessfulCallback);
            Assert.Equal(1, harness.Provider.ExchangeCount);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task Rejected_provider_response_also_requires_browser_binding_and_consumes_state()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();

        using var invalid = await harness.Callback(login.State, error: "access_denied");
        AssertCallbackRejected(invalid, "invalid_callback");

        using var rejected = await harness.Callback(login, error: "access_denied");
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Equal("/oauth/error", rejected.Headers.Location!.AbsolutePath);
        Assert.Equal(
            "access_denied",
            QueryHelpers.ParseQuery(rejected.Headers.Location.Query)["error"].ToString());
        AssertNoSessionCookie(rejected);

        using var replay = await harness.Callback(login);
        AssertCallbackRejected(replay, "invalid_callback");
        Assert.Equal(0, harness.Provider.ExchangeCount);
    }

    [Fact]
    public async Task Full_state_store_rejects_login_before_discovery_and_failed_discovery_releases_capacity()
    {
        using var harness = new Harness(capacity: 1);
        harness.Provider.FailDiscovery = true;
        using var failed = await harness.Client.GetAsync("/api/auth/login");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(1, harness.Provider.Discoveries);

        harness.Provider.FailDiscovery = false;
        _ = await harness.BeginLogin();
        var discoveries = harness.Provider.Discoveries;
        using var full = await harness.Client.GetAsync("/api/auth/login");
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(discoveries, harness.Provider.Discoveries);
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
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("unsigned")]
    [InlineData("audience")]
    [InlineData("missing_nonce")]
    [InlineData("missing_subject")]
    public async Task Invalid_id_tokens_cannot_create_a_session_cookie(string failure)
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        harness.Provider.IdTokenFailure = failure;

        using var response = await harness.Callback(login);

        AssertCallbackRejected(response, "token_exchange_failed");
        AssertNoSessionCookie(response);
        Assert.Equal(1, harness.Provider.ExchangeCount);
    }

    [Theory]
    [InlineData("http_error")]
    [InlineData("invalid_json")]
    public async Task Token_endpoint_errors_and_invalid_json_cannot_create_a_session_cookie(string failure)
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        harness.Provider.TokenResponseFailure = failure;

        using var response = await harness.Callback(login);

        AssertCallbackRejected(response, "token_exchange_failed");
        AssertNoSessionCookie(response);
        Assert.Equal(1, harness.Provider.ExchangeCount);
    }

    [Fact]
    public async Task Tampered_state_is_rejected_without_consuming_the_legitimate_flow()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        var tamperedState = login.State[..^1] + (login.State[^1] == 'A' ? 'B' : 'A');

        using var tampered = await harness.Callback(tamperedState, login.Cookies);
        AssertCallbackRejected(tampered, "invalid_callback");
        AssertNoSessionCookie(tampered);
        Assert.Equal(0, harness.Provider.ExchangeCount);

        using var legitimate = await harness.Callback(login);
        AssertCallbackSucceeded(legitimate);
        Assert.Equal(1, harness.Provider.ExchangeCount);
    }

    [Fact]
    public async Task Missing_native_correlation_cookie_is_rejected_after_binding_is_consumed()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        var withoutCorrelation = login.CookiesWithout(CorrelationCookiePrefix);

        using var missing = await harness.Callback(login.State, withoutCorrelation);
        AssertCallbackRejected(missing, "invalid_callback");
        AssertNoSessionCookie(missing);
        Assert.Equal(0, harness.Provider.ExchangeCount);

        using var replay = await harness.Callback(login);
        AssertCallbackRejected(replay, "invalid_callback");
        Assert.Equal(0, harness.Provider.ExchangeCount);
    }

    [Fact]
    public async Task Missing_native_nonce_cookie_is_rejected_after_code_exchange()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        var withoutNonce = login.CookiesWithout(NonceCookiePrefix);

        using var missing = await harness.Callback(login.State, withoutNonce);
        AssertCallbackRejected(missing, "token_exchange_failed");
        AssertNoSessionCookie(missing);
        Assert.Equal(1, harness.Provider.ExchangeCount);

        using var replay = await harness.Callback(login);
        AssertCallbackRejected(replay, "invalid_callback");
        Assert.Equal(1, harness.Provider.ExchangeCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Logout_revokes_both_tokens_and_always_removes_the_local_cookie(
        bool failRevocation,
        bool localOnly)
    {
        using var harness = new Harness(signOutProvider: !localOnly);
        var login = await harness.BeginLogin();
        using var signedIn = await harness.Callback(login);
        AssertCallbackSucceeded(signedIn);
        var sessionCookie = SessionCookie(signedIn);

        harness.Provider.FailRevocation = failRevocation;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", sessionCookie);
        using var loggedOut = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, loggedOut.StatusCode);
        Assert.Equal(
            new[] { "refresh_token:refresh", "access_token:access" },
            harness.Provider.Revocations);
        Assert.Contains(
            loggedOut.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".NexusAuth.Workbench=;", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(await loggedOut.Content.ReadAsStringAsync());
        var url = body.RootElement.GetProperty("logoutUrl").GetString();
        if (localOnly)
        {
            Assert.Equal(string.Empty, url);
        }
        else
        {
            var endpoint = new Uri(url!);
            Assert.Equal("/custom/logout", endpoint.AbsolutePath);
            Assert.Equal("retained", QueryHelpers.ParseQuery(endpoint.Query)["existing"].ToString());
            Assert.Equal(
                PostLogoutRedirectUri,
                QueryHelpers.ParseQuery(endpoint.Query)["post_logout_redirect_uri"].ToString());
        }
    }

    [Fact]
    public async Task Discovery_failure_during_logout_cannot_prevent_local_signout()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        using var signedIn = await harness.Callback(login);
        AssertCallbackSucceeded(signedIn);
        var cookie = SessionCookie(signedIn);
        harness.Provider.FailDiscovery = true;
        harness.Provider.FailRevocation = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", cookie);
        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".NexusAuth.Workbench=;", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, body.RootElement.GetProperty("logoutUrl").GetString());
    }

    [Fact]
    public async Task Unresponsive_discovery_during_logout_is_bounded_and_still_clears_the_cookie()
    {
        using var harness = new Harness();
        var login = await harness.BeginLogin();
        using var signedIn = await harness.Callback(login);
        AssertCallbackSucceeded(signedIn);
        var cookie = SessionCookie(signedIn);
        var discoveriesBeforeLogout = harness.Provider.Discoveries;
        harness.Provider.NeverCompleteDiscovery = true;
        harness.Provider.FailRevocation = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", cookie);
        var stopwatch = Stopwatch.StartNew();
        using var response = await harness.Client.SendAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(7));
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(7),
            $"Logout exceeded its discovery budget: {stopwatch.Elapsed}.");
        Assert.Equal(discoveriesBeforeLogout + 1, harness.Provider.Discoveries);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".NexusAuth.Workbench=;", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, body.RootElement.GetProperty("logoutUrl").GetString());
    }

    [Fact]
    public async Task State_expires_and_frees_capacity_without_consuming_another_browsers_flow()
    {
        var clock = new TestClock();
        using var harness = new Harness(capacity: 1, clock: clock);
        var expired = await harness.BeginLogin();
        clock.Advance(TimeSpan.FromMinutes(10));

        using var stale = await harness.Callback(expired);
        AssertCallbackRejected(stale, "invalid_callback");
        Assert.Equal(0, harness.Provider.ExchangeCount);

        var current = await harness.BeginLogin();
        using var replay = await harness.Callback(expired);
        AssertCallbackRejected(replay, "invalid_callback");
        using var completed = await harness.Callback(current);
        AssertCallbackSucceeded(completed);
        Assert.Equal(1, harness.Provider.ExchangeCount);
    }

    private static void AssertCallbackSucceeded(HttpResponseMessage response)
    {
        Assert.True(IsSuccessfulCallback(response), response.Headers.Location?.ToString());
        AssertSessionCookie(response);
    }

    private static bool IsSuccessfulCallback(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Redirect
        && response.Headers.Location?.ToString() == PostLogoutRedirectUri.TrimEnd('/') + "/auth/callback";

    private static void AssertCallbackRejected(HttpResponseMessage response, string expectedError)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/login", response.Headers.Location.AbsolutePath);
        Assert.Equal(
            expectedError,
            QueryHelpers.ParseQuery(response.Headers.Location.Query)["error"].ToString());
    }

    private static void AssertSessionCookie(HttpResponseMessage response) =>
        _ = SessionCookie(response);

    private static string SessionCookie(HttpResponseMessage response)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".NexusAuth.Workbench=", StringComparison.Ordinal)
                && CookieValue(value).Length > 0);
        return header.Split(';', 2)[0];
    }

    private static void AssertNoSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            return;

        Assert.DoesNotContain(
            headers,
            value => value.StartsWith(".NexusAuth.Workbench=", StringComparison.Ordinal)
                && CookieValue(value).Length > 0);
    }

    private static string CookieValue(string setCookieHeader)
    {
        var valueStart = setCookieHeader.IndexOf('=') + 1;
        var valueEnd = setCookieHeader.IndexOf(';', valueStart);
        return valueEnd < 0
            ? setCookieHeader[valueStart..]
            : setCookieHeader[valueStart..valueEnd];
    }

    private sealed record LoginFlow(
        Uri AuthorizeUrl,
        string State,
        string Nonce,
        IReadOnlyDictionary<string, string> Cookies,
        IReadOnlyList<string> SetCookieHeaders,
        string BindingCookieHeader)
    {
        public Dictionary<string, string> CookiesWithout(string cookiePrefix) =>
            Cookies
                .Where(item => !item.Key.StartsWith(cookiePrefix, StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
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
        private readonly RSA trustedRsa;
        private readonly RSA untrustedRsa;

        public ProviderStub Provider { get; }
        public HttpClient Client { get; }

        public Harness(
            int capacity = 10_000,
            bool signOutProvider = true,
            TimeProvider? clock = null)
        {
            trustedRsa = RSA.Create(2048);
            untrustedRsa = RSA.Create(2048);
            var trustedKey = new RsaSecurityKey(trustedRsa) { KeyId = "trusted-test-key" };
            var untrustedKey = new RsaSecurityKey(untrustedRsa) { KeyId = "trusted-test-key" };
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Authority,
                AuthorizationEndpoint = Authority + "/authorize",
                TokenEndpoint = Authority + "/token",
                EndSessionEndpoint = Authority + "/custom/logout?existing=retained",
            };
            configuration.SigningKeys.Add(trustedKey);
            var metadata = new TestMetadataManager(configuration);
            Provider = new ProviderStub(metadata, trustedKey, untrustedKey);
            var backchannel = new ProviderBackchannelHandler(Provider);

            factory = new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing")
                    .UseSetting("Auth:Authority", Authority)
                    .UseSetting("Auth:Audience", Audience)
                    .UseSetting("Auth:ClientId", ClientId)
                    .UseSetting("Auth:ClientSecret", "test-only-secret")
                    .UseSetting("Auth:RedirectUri", RedirectUri)
                    .UseSetting("Auth:PostLogoutRedirectUri", PostLogoutRedirectUri)
                    .UseSetting("Auth:Scope", "openid profile offline_access")
                    .UseSetting("Auth:SignOutProvider", signOutProvider.ToString());
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWorkbenchTokenClient>();
                    services.AddScoped<IWorkbenchTokenClient>(_ => Provider);
                    services.Configure<OpenIdConnectFlowStateOptions>(options => options.Capacity = capacity);
                    services.AddSingleton(clock ?? TimeProvider.System);
                    services.AddHttpClient(WorkbenchTokenClient.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => backchannel);
                    services.AddOptions<OpenIdConnectOptions>(
                            WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
                        .PostConfigure<IHttpClientFactory>((options, clients) =>
                        {
                            options.ConfigurationManager = metadata;
                            options.Backchannel = clients.CreateClient(WorkbenchTokenClient.HttpClientName);
                        });
                    RemoveDiscoveryWarmup(services);
                    services.PostConfigure<CookieAuthenticationOptions>(
                        WorkbenchAuthenticationDefaults.CookieScheme,
                        options =>
                        {
                            options.EventsType = null;
                            options.Events = new CookieAuthenticationEvents();
                        });
                });
            });
            Client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://dashboard.example.test"),
                AllowAutoRedirect = false,
                HandleCookies = false,
            });
        }

        public async Task<LoginFlow> BeginLogin()
        {
            using var response = await Client.GetAsync("/api/auth/login");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var url = new Uri(body.RootElement.GetProperty("authorizeUrl").GetString()!);
            var query = QueryHelpers.ParseQuery(url.Query);
            var state = query["state"].ToString();
            var nonce = query["nonce"].ToString();
            Assert.False(string.IsNullOrWhiteSpace(state));
            Assert.False(string.IsNullOrWhiteSpace(nonce));

            var setCookieHeaders = response.Headers.GetValues("Set-Cookie").ToArray();
            var cookies = setCookieHeaders.ToDictionary(
                header => header[..header.IndexOf('=')],
                header => header[(header.IndexOf('=') + 1)..header.IndexOf(';')]);
            var bindingCookieHeader = Assert.Single(
                setCookieHeaders,
                header => header.StartsWith(WorkbenchBindingCookiePrefix, StringComparison.Ordinal));
            Assert.Single(cookies.Keys, name => name.StartsWith(CorrelationCookiePrefix, StringComparison.Ordinal));
            Assert.Single(cookies.Keys, name => name.StartsWith(NonceCookiePrefix, StringComparison.Ordinal));

            Provider.RegisterLogin(state, nonce);
            return new LoginFlow(url, state, nonce, cookies, setCookieHeaders, bindingCookieHeader);
        }

        public Task<HttpResponseMessage> Callback(LoginFlow login, string? error = null) =>
            Callback(login.State, login.Cookies, error);

        public async Task<HttpResponseMessage> Callback(
            string state,
            IReadOnlyDictionary<string, string>? cookies = null,
            string? error = null)
        {
            var query = new Dictionary<string, string?>
            {
                ["state"] = state,
                ["code"] = error is null ? Provider.CodeFor(state) : null,
                ["error"] = error,
            };
            var url = QueryHelpers.AddQueryString("/signin-oidc", query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookies is { Count: > 0 })
            {
                request.Headers.Add(
                    "Cookie",
                    string.Join("; ", cookies.Select(item => $"{item.Key}={item.Value}")));
            }

            return await Client.SendAsync(request);
        }

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
            trustedRsa.Dispose();
            untrustedRsa.Dispose();
        }

        private static void RemoveDiscoveryWarmup(IServiceCollection services)
        {
            var descriptors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType?.Name.EndsWith(
                        "DiscoveryWarmupService",
                        StringComparison.Ordinal) == true)
                .ToArray();
            foreach (var descriptor in descriptors)
                services.Remove(descriptor);
        }
    }

    private sealed class ProviderStub(
        TestMetadataManager metadata,
        SecurityKey trustedKey,
        SecurityKey untrustedKey) : IWorkbenchTokenClient
    {
        private readonly ConcurrentDictionary<string, string> nonces = new(StringComparer.Ordinal);
        private readonly object exchangeGate = new();
        private readonly object revocationGate = new();
        private readonly List<ExchangeCall> exchanges = [];
        private int exchangeCount;

        public bool FailDiscovery
        {
            get => metadata.Fail;
            set => metadata.Fail = value;
        }

        public bool NeverCompleteDiscovery
        {
            get => metadata.NeverCompleteDiscovery;
            set => metadata.NeverCompleteDiscovery = value;
        }

        public bool FailRevocation { get; set; }
        public string? IdTokenFailure { get; set; }
        public string? TokenResponseFailure { get; set; }
        public int Discoveries => metadata.Calls;
        public int ExchangeCount => Volatile.Read(ref exchangeCount);
        public IReadOnlyList<ExchangeCall> Exchanges
        {
            get
            {
                lock (exchangeGate)
                    return exchanges.ToArray();
            }
        }

        public List<string> Revocations { get; } = [];

        public void RegisterLogin(string state, string nonce) => nonces[state] = nonce;

        public string CodeFor(string state) => "code:" + state;

        public async Task<HttpResponseMessage> HandleTokenRequestAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Authority + "/token", request.RequestUri?.GetLeftPart(UriPartial.Path));
            Assert.True(request.Headers.Authorization?.Scheme == "Basic");
            Assert.Equal(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":test-only-secret")),
                request.Headers.Authorization.Parameter);
            Assert.Equal("application/x-www-form-urlencoded", request.Content?.Headers.ContentType?.MediaType);

            var body = await request.Content!.ReadAsStringAsync(ct);
            var form = QueryHelpers.ParseQuery(body);
            var code = form["code"].ToString();
            var codeVerifier = form["code_verifier"].ToString();
            var redirectUri = form["redirect_uri"].ToString();
            Assert.Equal("authorization_code", form["grant_type"].ToString());
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.False(string.IsNullOrWhiteSpace(codeVerifier));
            Assert.Equal(RedirectUri, redirectUri);

            Interlocked.Increment(ref exchangeCount);
            lock (exchangeGate)
                exchanges.Add(new ExchangeCall(code, codeVerifier, redirectUri));

            if (TokenResponseFailure == "http_error")
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":"invalid_grant","error_description":"provider-secret-detail"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (TokenResponseFailure == "invalid_json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{not-json", Encoding.UTF8, "application/json"),
                };
            }

            var state = code["code:".Length..];
            if (!nonces.TryGetValue(state, out var nonce))
                throw new InvalidOperationException("No nonce is registered for this login.");

            var idToken = CreateIdToken(nonce);
            var response = new
            {
                access_token = "access",
                refresh_token = "refresh",
                id_token = idToken,
                token_type = "Bearer",
                expires_in = 120,
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        public Task<WorkbenchTokenResult> RefreshTokensAsync(
            string refreshToken,
            CancellationToken ct) => Task.FromResult(new WorkbenchTokenResult(
                "refreshed-access",
                "refreshed-refresh",
                null,
                120));

        public Task<WorkbenchIntrospectionResult> IntrospectAccessTokenAsync(
            string accessToken,
            CancellationToken ct) => Task.FromResult(new WorkbenchIntrospectionResult(
                true,
                "alice",
                ClientId));

        public Task RevokeTokenAsync(
            string token,
            string tokenTypeHint,
            CancellationToken ct)
        {
            lock (revocationGate)
                Revocations.Add(tokenTypeHint + ":" + token);
            if (FailRevocation)
                throw new HttpRequestException("Provider unavailable");
            return Task.CompletedTask;
        }

        private string CreateIdToken(string nonce)
        {
            var now = DateTime.UtcNow;
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Iat,
                    new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64),
                new("name", "Alice"),
            };
            if (IdTokenFailure != "missing_subject")
                claims.Add(new Claim(JwtRegisteredClaimNames.Sub, "alice"));
            if (IdTokenFailure != "missing_nonce")
            {
                claims.Add(new Claim(
                    JwtRegisteredClaimNames.Nonce,
                    IdTokenFailure == "nonce" ? nonce + "-wrong" : nonce));
            }

            SigningCredentials? credentials = IdTokenFailure switch
            {
                "unsigned" => null,
                "signature" => new SigningCredentials(untrustedKey, SecurityAlgorithms.RsaSha256),
                _ => new SigningCredentials(trustedKey, SecurityAlgorithms.RsaSha256),
            };
            var token = new JwtSecurityToken(
                IdTokenFailure == "issuer" ? "https://wrong-issuer.example.test" : Authority,
                IdTokenFailure == "audience" ? "different-audience" : ClientId,
                claims,
                IdTokenFailure == "expired" ? now.AddMinutes(-10) : now.AddMinutes(-1),
                IdTokenFailure == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
                credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    private sealed class ProviderBackchannelHandler(ProviderStub provider) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            provider.HandleTokenRequestAsync(request, cancellationToken);
    }

    private sealed class TestMetadataManager(OpenIdConnectConfiguration configuration)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly TaskCompletionSource<OpenIdConnectConfiguration> neverCompleting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        private volatile bool fail;
        private volatile bool neverCompleteDiscovery;

        public bool Fail
        {
            get => fail;
            set => fail = value;
        }

        public bool NeverCompleteDiscovery
        {
            get => neverCompleteDiscovery;
            set => neverCompleteDiscovery = value;
        }

        public int Calls => Volatile.Read(ref calls);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            if (Fail)
            {
                return Task.FromException<OpenIdConnectConfiguration>(
                    new HttpRequestException("Provider unavailable"));
            }
            return NeverCompleteDiscovery
                ? WaitForUnresponsiveProviderAsync(cancel)
                : Task.FromResult(configuration);
        }

        public void RequestRefresh()
        {
        }

        private async Task<OpenIdConnectConfiguration> WaitForUnresponsiveProviderAsync(
            CancellationToken cancellationToken)
        {
            await neverCompleting.Task.WaitAsync(cancellationToken);
            return configuration;
        }
    }

    private sealed record ExchangeCall(string Code, string CodeVerifier, string RedirectUri);
}
