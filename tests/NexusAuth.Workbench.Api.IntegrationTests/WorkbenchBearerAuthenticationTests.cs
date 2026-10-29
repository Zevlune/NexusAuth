using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchBearerAuthenticationTests(
    WorkbenchAdministrationAuthorizationFixture fixture)
    : IClassFixture<WorkbenchAdministrationAuthorizationFixture>
{
    [Fact]
    public async Task Cookie_and_bearer_tokens_each_authenticate()
    {
        using var bearer = fixture.CreateBearerClient(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        using var bearerResponse = await bearer.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);

        using var cookie = await CreateSignedInCookieClientAsync(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        using var cookieResponse = await cookie.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, cookieResponse.StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("id_token")]
    [InlineData("missing_token_use")]
    public async Task Jwt_validation_rejects_invalid_tokens_with_a_bearer_challenge(string invalidCase)
    {
        var now = DateTimeOffset.UtcNow;
        using var wrongRsa = RSA.Create(2048);
        var token = invalidCase switch
        {
            "issuer" => fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                issuer: "https://wrong-issuer.example.test"),
            "audience" => fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                audience: "wrong-audience"),
            "signature" => fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                signingKeyOverride: new RsaSecurityKey(wrongRsa)),
            "expired" => fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                notBefore: now.AddMinutes(-10),
                expires: now.AddMinutes(-5)),
            "id_token" => fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                tokenUse: "id_token"),
            "missing_token_use" => fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                tokenUse: null),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null),
        };
        using var client = fixture.CreateClientWithBearerToken(token);

        using var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertBearerChallenge(response);
    }

    [Fact]
    public async Task Valid_bearer_token_takes_precedence_over_an_existing_cookie()
    {
        using var client = await CreateSignedInCookieClientAsync(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.CreateBearerToken(WorkbenchAdministrationAuthorizationFixture.OrdinaryUserId));

        using var bearerIdentity = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, bearerIdentity.StatusCode);
        using var bearerBody = JsonDocument.Parse(await bearerIdentity.Content.ReadAsStringAsync());
        Assert.True(bearerBody.RootElement.GetProperty("isAuthenticated").GetBoolean());
        Assert.False(bearerBody.RootElement.GetProperty("isAdministrator").GetBoolean());

        client.DefaultRequestHeaders.Authorization = null;
        using var cookieIdentity = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, cookieIdentity.StatusCode);
    }

    [Fact]
    public async Task Invalid_bearer_token_does_not_fall_back_to_a_valid_cookie()
    {
        using var client = await CreateSignedInCookieClientAsync(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            fixture.CreateBearerToken(
                WorkbenchAdministrationAuthorizationFixture.AdministratorId,
                issuer: "https://wrong-issuer.example.test"));

        using var denied = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        AssertBearerChallenge(denied);

        client.DefaultRequestHeaders.Authorization = null;
        using var cookieStillWorks = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, cookieStillWorks.StatusCode);
    }

    [Theory]
    [InlineData("Basic credentials")]
    [InlineData("Token opaque-value")]
    [InlineData("Bearer")]
    public async Task Any_authorization_header_selects_bearer_without_cookie_fallback(
        string authorization)
    {
        using var client = await CreateSignedInCookieClientAsync(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

        using var denied = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        AssertBearerChallenge(denied);
        client.DefaultRequestHeaders.Remove("Authorization");
        using var cookieStillWorks = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, cookieStillWorks.StatusCode);
    }

    [Fact]
    public async Task Revoked_bearer_token_is_rejected_by_the_real_user_token_validator()
    {
        const string jti = "revoked-bearer-token";
        fixture.BlacklistToken(jti);
        var token = fixture.CreateBearerToken(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId,
            jti: jti);
        using var client = fixture.CreateClientWithBearerToken(token);

        using var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertBearerChallenge(response);
    }

    [Fact]
    public async Task Bearer_logout_revokes_the_access_token_and_preserves_an_existing_cookie()
    {
        var token = fixture.CreateBearerToken(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId,
            jti: "bearer-logout-token");
        using var client = await CreateSignedInCookieClientAsync(
            WorkbenchAdministrationAuthorizationFixture.AdministratorId);
        fixture.ResetTokenRevocations();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var revocation = Assert.Single(fixture.RevokedTokens);
        Assert.Equal(token, revocation.Token);
        Assert.Equal("access_token", revocation.TokenTypeHint);
        using var body = JsonDocument.Parse(await logout.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, body.RootElement.GetProperty("logoutUrl").GetString());

        client.DefaultRequestHeaders.Authorization = null;
        using var cookieSession = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, cookieSession.StatusCode);
        using var cookieBody = JsonDocument.Parse(await cookieSession.Content.ReadAsStringAsync());
        Assert.True(cookieBody.RootElement.GetProperty("isAuthenticated").GetBoolean());
    }

    private async Task<HttpClient> CreateSignedInCookieClientAsync(Guid userId)
    {
        var client = fixture.CreateAnonymousClient();
        using var signIn = await client.GetAsync($"/__test/sign-in/{userId}");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        return client;
    }

    private static void AssertBearerChallenge(HttpResponseMessage response)
    {
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            challenge => string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
    }
}
