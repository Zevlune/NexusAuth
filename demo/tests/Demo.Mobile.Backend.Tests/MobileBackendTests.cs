using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Demo.Mobile.Backend.Tests;

public sealed class MobileBackendTests : IClassFixture<MobileBackendFixture>
{
    private readonly MobileBackendFixture fixture;

    public MobileBackendTests(MobileBackendFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Login_callback_exchange_refresh_and_logout_complete_with_pkce_and_app_state()
    {
        fixture.Provider.Reset();
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var flow = await StartLogin(client, fixture.Provider, "app-state-1");
        fixture.Provider.ExpectedNonce = flow.Nonce;
        var callback = await client.GetAsync($"/signin-oidc?state={flow.ProviderState}&code=auth-code");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var appCallback = new Uri(callback.Headers.Location!.ToString());
        Assert.Equal("nexusauth-demo", appCallback.Scheme);
        Assert.Equal(flow.AppState, GetQuery(appCallback, "state"));
        var ticket = GetQuery(appCallback, "ticket");

        var exchanged = await client.PostAsJsonAsync("/api/mobile/exchange", new { ticket, verifier = flow.Verifier });
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var session = await exchanged.Content.ReadFromJsonAsync<ExchangeResponse>();
        Assert.NotNull(session);
        Assert.Equal("user-1", session!.User.Sub);
        Assert.True(fixture.Provider.PkceValidated);

        using var authenticated = fixture.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionToken);
        var profile = await authenticated.GetFromJsonAsync<ProfileResponse>("/api/mobile/profile");
        Assert.Equal("user-1", profile!.User.Sub);

        var refreshed = await authenticated.PostAsync("/api/mobile/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshBody = await refreshed.Content.ReadFromJsonAsync<ProfileResponse>();
        Assert.True(refreshBody!.Refreshed);
        Assert.Contains("access-rotated", fixture.Provider.UserInfoTokens);

        var logout = await authenticated.PostAsync("/api/mobile/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("refresh-rotated", fixture.Provider.RevokedTokens);
        Assert.Contains("access-rotated", fixture.Provider.RevokedTokens);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authenticated.GetAsync("/api/mobile/profile")).StatusCode);
    }

    [Fact]
    public async Task Wrong_verifier_does_not_consume_ticket_but_correct_verifier_does()
    {
        fixture.Provider.Reset();
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var flow = await StartLogin(client, fixture.Provider, "replay-state");
        fixture.Provider.ExpectedNonce = flow.Nonce;
        var callback = await client.GetAsync($"/signin-oidc?state={flow.ProviderState}&code=auth-code");
        var ticket = GetQuery(new Uri(callback.Headers.Location!.ToString()), "ticket");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/mobile/exchange", new { ticket, verifier = NewVerifier() })).StatusCode);
        var exchange = await client.PostAsJsonAsync("/api/mobile/exchange", new { ticket, verifier = flow.Verifier });
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/mobile/exchange", new { ticket, verifier = flow.Verifier })).StatusCode);
    }

    [Fact]
    public async Task Callback_state_is_single_use_and_original_state_is_preserved_on_provider_error()
    {
        fixture.Provider.Reset();
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var flow = await StartLogin(client, fixture.Provider, "state-on-error");
        var failed = await client.GetAsync($"/signin-oidc?state={flow.ProviderState}&error=access_denied");
        Assert.Equal("nexusauth-demo", failed.Headers.Location!.Scheme);
        Assert.Equal("login_failed", GetQuery(new Uri(failed.Headers.Location!.ToString()), "error"));
        Assert.Equal(flow.AppState, GetQuery(new Uri(failed.Headers.Location!.ToString()), "state"));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/signin-oidc?state={flow.ProviderState}&code=auth-code")).StatusCode);
    }

    [Theory]
    [InlineData(ProviderTokenFailure.BadSignature)]
    [InlineData(ProviderTokenFailure.BadNonce)]
    [InlineData(ProviderTokenFailure.SubjectMismatch)]
    public async Task Callback_rejects_invalid_identity(ProviderTokenFailure failure)
    {
        fixture.Provider.Reset();
        fixture.Provider.TokenFailure = failure;
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var flow = await StartLogin(client, fixture.Provider, "identity-state");
        fixture.Provider.ExpectedNonce = flow.Nonce;
        var response = await client.GetAsync($"/signin-oidc?state={flow.ProviderState}&code=auth-code");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = new Uri(response.Headers.Location!.ToString());
        Assert.Equal("login_failed", GetQuery(location, "error"));
        Assert.Equal(flow.AppState, GetQuery(location, "state"));
    }

    [Fact]
    public async Task Unauthenticated_endpoints_return_401_and_rejected_refresh_closes_session()
    {
        fixture.Provider.Reset();
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/mobile/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/mobile/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/mobile/logout", null)).StatusCode);

        var flow = await StartLogin(client, fixture.Provider, "refresh-rejection");
        fixture.Provider.ExpectedNonce = flow.Nonce;
        var callback = await client.GetAsync($"/signin-oidc?state={flow.ProviderState}&code=auth-code");
        var ticket = GetQuery(new Uri(callback.Headers.Location!.ToString()), "ticket");
        var exchange = await client.PostAsJsonAsync("/api/mobile/exchange", new { ticket, verifier = flow.Verifier });
        var session = await exchange.Content.ReadFromJsonAsync<ExchangeResponse>();
        fixture.Provider.RejectRefresh = true;
        using var authenticated = fixture.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authenticated.PostAsync("/api/mobile/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authenticated.GetAsync("/api/mobile/profile")).StatusCode);
    }

    private static async Task<LoginFlowResult> StartLogin(HttpClient client, FakeProvider provider, string appState)
    {
        var verifier = NewVerifier();
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var response = await client.PostAsJsonAsync("/api/mobile/login", new { challenge, state = appState.PadRight(43, 'x') });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        var uri = new Uri(body!.AuthorizationUrl);
        provider.ExpectedCodeChallenge = GetQuery(uri, "code_challenge");
        Assert.Equal(43, provider.ExpectedCodeChallenge.Length);
        Assert.Equal("S256", GetQuery(uri, "code_challenge_method"));
        return new(verifier, GetQuery(uri, "state"), GetQuery(uri, "nonce"), appState.PadRight(43, 'x'));
    }

    private static string GetQuery(Uri uri, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)[name].ToString();

    private static string NewVerifier() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private sealed record LoginResponse(string AuthorizationUrl);
    private sealed record LoginFlowResult(string Verifier, string ProviderState, string Nonce, string AppState);
    private sealed record ExchangeResponse(string SessionToken, DateTimeOffset ExpiresAt, User User);
    private sealed record ProfileResponse(User User, DateTimeOffset ExpiresAt, bool Refreshed = false);
    private sealed record User(string Sub, string? Name, string? PreferredUsername, string? Email);
}

public sealed class MobileBackendFixture : WebApplicationFactory<Program>
{
    public FakeProvider Provider { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NexusAuth:Authority"] = FakeProvider.Authority,
            ["NexusAuth:ClientId"] = FakeProvider.ClientId,
            ["NexusAuth:ClientSecret"] = FakeProvider.ClientSecret,
            ["NexusAuth:RedirectUri"] = "http://localhost:5401/signin-oidc",
            ["NexusAuth:Scope"] = "openid profile email offline_access",
            ["Mobile:CallbackUri"] = "nexusauth-demo://auth/callback",
        })).ConfigureTestServices(services => services
            .AddHttpClient<Demo.Mobile.Backend.OidcClient>()
            .ConfigurePrimaryHttpMessageHandler(() => Provider));
    }
}

public enum ProviderTokenFailure { None, BadSignature, BadNonce, SubjectMismatch }

public sealed class FakeProvider : HttpMessageHandler
{
    public const string Authority = "http://localhost:5100";
    public const string ClientId = "demo-mobile-bff";
    public const string ClientSecret = "test-secret";
    private readonly RSA signingKey = RSA.Create(2048);
    private readonly RSA wrongKey = RSA.Create(2048);
    private readonly ConcurrentQueue<string> revokedTokens = new();
    private readonly ConcurrentQueue<string> userInfoTokens = new();
    public string? ExpectedNonce { get; set; }
    public string? ExpectedCodeChallenge { get; set; }
    public bool PkceValidated { get; private set; }
    public bool RejectRefresh { get; set; }
    public ProviderTokenFailure TokenFailure { get; set; }
    public IReadOnlyCollection<string> RevokedTokens => revokedTokens.ToArray();
    public IReadOnlyCollection<string> UserInfoTokens => userInfoTokens.ToArray();

    public void Reset()
    {
        while (revokedTokens.TryDequeue(out _)) { }
        while (userInfoTokens.TryDequeue(out _)) { }
        ExpectedNonce = null;
        ExpectedCodeChallenge = null;
        PkceValidated = false;
        RejectRefresh = false;
        TokenFailure = ProviderTokenFailure.None;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/.well-known/openid-configuration")
            return Json(new { issuer = Authority, authorization_endpoint = Authority + "/authorize", token_endpoint = Authority + "/token", userinfo_endpoint = Authority + "/userinfo", jwks_uri = Authority + "/jwks", revocation_endpoint = Authority + "/revoke" });
        if (request.Method == HttpMethod.Get && path == "/jwks")
            return Json(new { keys = new[] { PublicJwk(signingKey, "provider-key") } });
        if (request.Method == HttpMethod.Post && path == "/token")
            return await Token(request, cancellationToken);
        if (request.Method == HttpMethod.Get && path == "/userinfo")
        {
            var bearer = request.Headers.Authorization?.Parameter;
            userInfoTokens.Enqueue(bearer ?? "");
            return Json(new { sub = TokenFailure == ProviderTokenFailure.SubjectMismatch ? "different-user" : "user-1", name = "Test User", preferred_username = "test-user", email = "test@example.com" });
        }
        if (request.Method == HttpMethod.Post && path == "/revoke")
        {
            var fields = await request.Content!.ReadAsStringAsync(cancellationToken);
            var token = QueryHelpers.ParseQuery(fields)["token"].ToString();
            if (!string.IsNullOrEmpty(token)) revokedTokens.Enqueue(token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> Token(HttpRequestMessage request, CancellationToken ct)
    {
        var basic = request.Headers.Authorization?.Parameter;
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(ClientId) + ":" + Uri.EscapeDataString(ClientSecret))), basic);
        var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
        if (fields["grant_type"] == "refresh_token")
        {
            if (RejectRefresh || fields["refresh_token"] != "refresh-1") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return Json(new { access_token = "access-rotated", refresh_token = "refresh-rotated", id_token = (string?)null, expires_in = 3600, token_type = "Bearer" });
        }
        Assert.Equal("authorization_code", fields["grant_type"]);
        Assert.Equal("auth-code", fields["code"]);
        Assert.Equal("http://localhost:5401/signin-oidc", fields["redirect_uri"]);
        var verifier = fields["code_verifier"].ToString();
        Assert.False(string.IsNullOrEmpty(verifier));
        PkceValidated = verifier.Length >= 43 &&
            Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == ExpectedCodeChallenge;
        Assert.True(PkceValidated);
        var idTokenKey = TokenFailure == ProviderTokenFailure.BadSignature ? wrongKey : signingKey;
        var nonce = TokenFailure == ProviderTokenFailure.BadNonce ? "wrong-nonce" : ExpectedNonce;
        return Json(new { access_token = "access-initial", refresh_token = "refresh-1", id_token = CreateIdToken(idTokenKey, nonce!, "user-1"), expires_in = 3600, token_type = "Bearer" });
    }

    private string CreateIdToken(RSA key, string nonce, string subject)
    {
        var credentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "provider-key" }, SecurityAlgorithms.RsaSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Authority, ClientId,
            [new Claim("sub", subject), new Claim("nonce", nonce)], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), credentials));
    }

    private static object PublicJwk(RSA key, string kid)
    {
        var parameters = key.ExportParameters(false);
        return new { kty = "RSA", use = "sig", kid, alg = "RS256", n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) };
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
