using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using NexusAuth.Host.Controllers;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// Exercises the real authorization endpoint and consent Razor page with an in-memory consent
/// store. The server-side id is the security boundary; the browser never receives the stored
/// authorization query as writable form state.
/// </summary>
public sealed class ConsentPageTests : IClassFixture<WebApplicationFactory<AppWebModule>>, IDisposable
{
    private const string ClientId = ConsentPageTestHarness.ClientId;
    private const string RedirectUri = ConsentPageTestHarness.RedirectUri;
    private const string PkceChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string DefaultScope = OidcScopes.OpenId + " " + OidcScopes.Profile;
    private const string OfflineScope = OidcScopes.OpenId + " " + OidcScopes.OfflineAccess;
    private const string ExpandedScope = DefaultScope + " " + OidcScopes.Email;
    private readonly WebApplicationFactory<AppWebModule> baseFactory;
    private readonly ConsentPageTestHarness harness;

    public ConsentPageTests(WebApplicationFactory<AppWebModule> factory)
    {
        baseFactory = factory;
        harness = new ConsentPageTestHarness(factory);
    }

    [Fact]
    public async Task Consent_page_and_denial_allow_optional_parameters_to_be_omitted()
    {
        using var client = harness.CreateClient();
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = OAuthResponseTypes.Code,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = DefaultScope,
            ["code_challenge"] = PkceChallenge,
            ["code_challenge_method"] = PkceMethods.S256,
        });
        using var authorization = await client.GetAsync(url);
        using var pageResponse = await client.GetAsync(authorization.Headers.Location!);
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        var form = FindForm(await pageResponse.Content.ReadAsStringAsync(), "Deny");
        using var denied = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        var query = QueryHelpers.ParseQuery(denied.Headers.Location!.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.False(query.ContainsKey("state"));
    }

    [Fact]
    public async Task Authorize_redirects_to_the_consent_page_and_freezes_the_original_request()
    {
        using var client = harness.CreateClient();

        using var authorizeResponse = await client.GetAsync(AuthorizeUrl());

        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
        var consentLocation = Assert.IsType<Uri>(authorizeResponse.Headers.Location);
        Assert.Equal("/consent", GetLocalPath(consentLocation));
        var consentId = ParseLocalQuery(consentLocation)["id"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(consentId));

        var stored = harness.Store.GetRequest(consentId);
        Assert.Equal(harness.UserId, stored.UserId);
        Assert.Equal(InMemoryConsentStatus.Pending, stored.Status);
        var storedQuery = QueryHelpers.ParseQuery(stored.Query);
        Assert.Equal("code", storedQuery["response_type"].ToString());
        Assert.Equal(ClientId, storedQuery["client_id"].ToString());
        Assert.Equal(RedirectUri, storedQuery["redirect_uri"].ToString());
        Assert.Equal("openid profile", storedQuery["scope"].ToString());
        Assert.Equal("state-123", storedQuery["state"].ToString());
        Assert.Equal("nonce-1", storedQuery["nonce"].ToString());
        Assert.Equal("{}", storedQuery["claims"].ToString());
        Assert.Equal(PkceChallenge, storedQuery["code_challenge"].ToString());
        Assert.Equal("S256", storedQuery["code_challenge_method"].ToString());

        using var pageResponse = await client.GetAsync(consentLocation);
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.True(
            pageResponse.StatusCode == HttpStatusCode.OK,
            $"Consent GET returned {(int)pageResponse.StatusCode} {pageResponse.StatusCode}:{Environment.NewLine}{page}");
        var form = FindForm(page, "Approve");
        Assert.Equal(consentId, form.Fields["id"]);
        Assert.Equal("true", form.Fields["Remember"]);
        Assert.Contains("__RequestVerificationToken", form.Fields.Keys);
        Assert.DoesNotContain("client_id", form.Fields.Keys);
        Assert.DoesNotContain("redirect_uri", form.Fields.Keys);
        Assert.DoesNotContain("scope", form.Fields.Keys);
    }

    [Fact]
    public async Task Approve_uses_the_stored_request_saves_grant_and_rejects_marker_replay()
    {
        using var client = harness.CreateClient();
        var flow = await StartConsentFlowAsync(client);
        var form = FindForm(flow.Page, "Approve");

        // The page may only change the consent decision. Supplying authorization parameters
        // must not replace the request that was frozen before the user saw the page.
        form.Fields["Remember"] = "true";
        form.Fields["client_id"] = "attacker-client";
        form.Fields["redirect_uri"] = "https://attacker.example/callback";
        form.Fields["scope"] = ExpandedScope;

        using var approveResponse = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Redirect, approveResponse.StatusCode);
        var callbackLocation = Assert.IsType<Uri>(approveResponse.Headers.Location);
        var callbackQuery = ParseLocalQuery(callbackLocation);
        Assert.Equal(flow.Id, callbackQuery[AuthorizeController.ConsentParameterName].ToString());
        Assert.Equal(ClientId, callbackQuery["client_id"].ToString());
        Assert.Equal(RedirectUri, callbackQuery["redirect_uri"].ToString());
        Assert.Equal("openid profile", callbackQuery["scope"].ToString());
        Assert.Equal("state-123", callbackQuery["state"].ToString());

        using var callbackResponse = await client.GetAsync(callbackLocation);

        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        var clientRedirect = callbackResponse.Headers.Location!;
        Assert.Equal(RedirectUri, clientRedirect.GetLeftPart(UriPartial.Path));
        var codeQuery = QueryHelpers.ParseQuery(clientRedirect.Query);
        Assert.Equal("consent-code", codeQuery["code"].ToString());
        Assert.Equal("state-123", codeQuery["state"].ToString());
        Assert.Single(harness.AuthorizationService.GeneratedCodes);
        var grant = Assert.Single(harness.Store.Grants);
        Assert.Equal(harness.UserId, grant.UserId);
        Assert.Equal(ClientId, grant.ClientId);
        Assert.Equal("openid profile", grant.Scope);
        Assert.Equal("{}", grant.Claims);

        using var replayResponse = await client.GetAsync(callbackLocation);

        Assert.Equal(HttpStatusCode.Redirect, replayResponse.StatusCode);
        var replay = QueryHelpers.ParseQuery(replayResponse.Headers.Location!.Query);
        Assert.Equal("invalid_request", replay["error"].ToString());
        Assert.False(replay.ContainsKey("code"));
        Assert.Single(harness.AuthorizationService.GeneratedCodes);
    }

    [Fact]
    public async Task Tampered_authorization_argument_is_rejected_without_consuming_the_approval()
    {
        using var client = harness.CreateClient();
        var flow = await StartConsentFlowAsync(client);
        var form = FindForm(flow.Page, "Approve");

        using var approveResponse = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Redirect, approveResponse.StatusCode);
        var callbackLocation = Assert.IsType<Uri>(approveResponse.Headers.Location);
        var tamperedQuery = ParseLocalQuery(callbackLocation)
            .ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value.ToString(),
                StringComparer.Ordinal);
        tamperedQuery["scope"] = ExpandedScope;
        var tamperedLocation = QueryHelpers.AddQueryString(GetLocalPath(callbackLocation), tamperedQuery);

        using var tamperedResponse = await client.GetAsync(tamperedLocation);

        Assert.Equal(HttpStatusCode.Redirect, tamperedResponse.StatusCode);
        var error = QueryHelpers.ParseQuery(tamperedResponse.Headers.Location!.Query);
        Assert.Equal("invalid_request", error["error"].ToString());
        Assert.Equal("state-123", error["state"].ToString());
        Assert.False(error.ContainsKey("code"));
        Assert.Equal(InMemoryConsentStatus.Approved, harness.Store.GetStatus(flow.Id));
        Assert.Empty(harness.AuthorizationService.GeneratedCodes);

        using var originalResponse = await client.GetAsync(callbackLocation);

        Assert.Equal(HttpStatusCode.Redirect, originalResponse.StatusCode);
        var code = QueryHelpers.ParseQuery(originalResponse.Headers.Location!.Query);
        Assert.Equal("consent-code", code["code"].ToString());
        Assert.Single(harness.AuthorizationService.GeneratedCodes);
    }

    [Fact]
    public async Task Deny_redirects_to_the_client_with_access_denied_and_state()
    {
        using var client = harness.CreateClient();
        var flow = await StartConsentFlowAsync(client);
        var form = FindForm(flow.Page, "Deny");

        using var response = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(RedirectUri, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("The resource owner denied the request.", query["error_description"].ToString());
        Assert.Equal("state-123", query["state"].ToString());
        Assert.Equal(InMemoryConsentStatus.Consumed, harness.Store.GetStatus(flow.Id));
        Assert.Empty(harness.AuthorizationService.GeneratedCodes);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected_without_approving_the_request()
    {
        using var client = harness.CreateClient();
        var flow = await StartConsentFlowAsync(client);
        var form = FindForm(flow.Page, "Approve");
        form.Fields.Remove("__RequestVerificationToken");

        using var response = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InMemoryConsentStatus.Pending, harness.Store.GetStatus(flow.Id));
        Assert.Equal(0, harness.Store.ApproveCalls);
    }

    [Fact]
    public async Task Remembered_grant_skips_the_consent_page()
    {
        await harness.Store.SaveGrantAsync(
            harness.UserId,
            ClientId,
            DefaultScope,
            "{}",
            CancellationToken.None);
        using var client = harness.CreateClient();

        using var response = await client.GetAsync(AuthorizeUrl(prompt: null));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(RedirectUri, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("consent-code", query["code"].ToString());
        Assert.Equal("state-123", query["state"].ToString());
        Assert.Equal(0, harness.Store.CreateCalls);
        Assert.Equal(1, harness.Store.HasGrantCalls);
        var generated = Assert.Single(harness.AuthorizationService.GeneratedCodes);
        Assert.Equal("openid profile", generated.Scope);
        Assert.Equal("{}", generated.Claims);
    }

    [Theory]
    [InlineData(ExpandedScope, "{}")]
    [InlineData(DefaultScope, "{\"id_token\":{\"email\":{\"essential\":true}}}")]
    public async Task Changed_scope_or_claims_requires_consent_again(
        string requestedScope,
        string requestedClaims)
    {
        await harness.Store.SaveGrantAsync(
            harness.UserId,
            ClientId,
            DefaultScope,
            "{}",
            CancellationToken.None);
        using var client = harness.CreateClient();

        var flow = await StartConsentFlowAsync(
            client,
            requestedScope,
            prompt: null,
            requestedClaims);

        var storedQuery = QueryHelpers.ParseQuery(harness.Store.GetRequest(flow.Id).Query);
        Assert.Equal(requestedScope, storedQuery["scope"].ToString());
        Assert.Equal(requestedClaims, storedQuery["claims"].ToString());
        Assert.Equal(1, harness.Store.CreateCalls);
        Assert.Equal(1, harness.Store.HasGrantCalls);
    }

    [Fact]
    public async Task Offline_access_requires_consent_even_when_client_consent_is_disabled()
    {
        using var noConsentHarness = new ConsentPageTestHarness(baseFactory, requireConsent: false);
        using var client = noConsentHarness.CreateClient();

        using var response = await client.GetAsync(AuthorizeUrl(
            scope: OfflineScope,
            prompt: null));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var consentLocation = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("/consent", GetLocalPath(consentLocation));
        var id = ParseLocalQuery(consentLocation)["id"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        using var pageResponse = await client.GetAsync(consentLocation);
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.True(
            pageResponse.StatusCode == HttpStatusCode.OK,
            $"Consent GET returned {(int)pageResponse.StatusCode} {pageResponse.StatusCode}:{Environment.NewLine}{page}");
        Assert.NotNull(FindForm(page, "Approve"));
        Assert.Equal(1, noConsentHarness.Store.CreateCalls);
        Assert.Empty(noConsentHarness.AuthorizationService.GeneratedCodes);
    }

    [Fact]
    public async Task Prompt_none_without_a_grant_returns_consent_required_without_creating_a_page_request()
    {
        using var client = harness.CreateClient();

        using var response = await client.GetAsync(AuthorizeUrl(
            scope: OfflineScope,
            prompt: OidcPromptValues.None));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(RedirectUri, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("consent_required", query["error"].ToString());
        Assert.Equal("state-123", query["state"].ToString());
        Assert.Equal(0, harness.Store.CreateCalls);
        Assert.Equal(0, harness.Store.HasGrantCalls);
        Assert.Empty(harness.AuthorizationService.GeneratedCodes);
    }

    private async Task<ConsentFlow> StartConsentFlowAsync(
        HttpClient client,
        string scope = DefaultScope,
        string? prompt = OidcPromptValues.Consent,
        string claims = "{}")
    {
        using var authorizeResponse = await client.GetAsync(AuthorizeUrl(scope, prompt, claims));
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
        var consentLocation = Assert.IsType<Uri>(authorizeResponse.Headers.Location);
        Assert.Equal("/consent", GetLocalPath(consentLocation));
        var id = ParseLocalQuery(consentLocation)["id"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        using var pageResponse = await client.GetAsync(consentLocation);
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.True(
            pageResponse.StatusCode == HttpStatusCode.OK,
            $"Consent GET returned {(int)pageResponse.StatusCode} {pageResponse.StatusCode}:{Environment.NewLine}{page}");
        Assert.DoesNotContain("class=\"error\"", page);
        return new ConsentFlow(id, page);
    }

    private static string AuthorizeUrl(
        string scope = DefaultScope,
        string? prompt = OidcPromptValues.Consent,
        string claims = "{}")
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = OAuthResponseTypes.Code,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = scope,
            ["state"] = "state-123",
            ["nonce"] = "nonce-1",
            ["claims"] = claims,
            ["response_mode"] = OAuthResponseModes.Query,
            ["code_challenge"] = PkceChallenge,
            ["code_challenge_method"] = PkceMethods.S256,
        };
        if (prompt is not null)
            query["prompt"] = prompt;
        return QueryHelpers.AddQueryString("/connect/authorize", query);
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> ParseLocalQuery(Uri localUri)
    {
        var uri = localUri.IsAbsoluteUri
            ? localUri
            : new Uri("https://issuer.example" + localUri.OriginalString);
        return QueryHelpers.ParseQuery(uri.Query);
    }

    private static string GetLocalPath(Uri localUri) =>
        localUri.IsAbsoluteUri
            ? localUri.AbsolutePath
            : localUri.OriginalString.Split('?', 2)[0];

    private static ConsentForm FindForm(string page, string handler)
    {
        var formMatch = Regex.Match(
            page,
            $"<form[^>]*action=\"(?<action>[^\"]*handler={handler}[^\"]*)\"[^>]*>(?<body>.*?)</form>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        Assert.True(formMatch.Success, $"The {handler} form was not rendered.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Regex.Matches(formMatch.Groups["body"].Value, "<input[^>]*>", RegexOptions.IgnoreCase))
        {
            var name = Regex.Match(input.Value, "name=\"(?<v>[^\"]*)\"").Groups["v"].Value;
            var value = Regex.Match(input.Value, "value=\"(?<v>[^\"]*)\"").Groups["v"].Value;
            if (!string.IsNullOrEmpty(name))
                fields[name] = WebUtility.HtmlDecode(value);
        }

        return new ConsentForm(WebUtility.HtmlDecode(formMatch.Groups["action"].Value), fields);
    }

    public void Dispose() => harness.Dispose();

    private sealed record ConsentFlow(string Id, string Page);

    private sealed record ConsentForm(string Action, Dictionary<string, string> Fields);
}
