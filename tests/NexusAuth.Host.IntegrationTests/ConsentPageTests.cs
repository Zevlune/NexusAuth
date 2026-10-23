using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusAuth.Application.Clients;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

/// <summary>
/// Drives the real consent Razor page: GET renders the request, and both forms must post the
/// complete authorization request back with the OAuth parameter names the page model binds.
/// </summary>
public sealed class ConsentPageTests : IClassFixture<WebApplicationFactory<AppWebModule>>
{
    private const string TestUserHeader = "X-Test-User";
    private const string RedirectUri = "https://client.example/callback";
    private readonly WebApplicationFactory<AppWebModule> factory;

    public ConsentPageTests(WebApplicationFactory<AppWebModule> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("BootstrapAdmin:Username", string.Empty)
            .UseSetting("BootstrapAdmin:Password", string.Empty)
            .ConfigureTestServices(services =>
            {
                services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(BootstrapAdminHostedService)));
                services.RemoveAll<IClientService>();
                services.AddSingleton(HardeningClientServices.AcceptingAuthorizeRequests());
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestUserStartupFilter>();
            }));
    }

    [Fact]
    public async Task Approve_posts_the_full_authorization_request_back_to_the_authorize_endpoint()
    {
        using var client = CreateClient();
        var page = await GetConsentPageAsync(client, "query");
        var form = FindForm(page, "Approve");

        Assert.Equal("web-client", form.Fields["client_id"]);
        Assert.Equal(RedirectUri, form.Fields["redirect_uri"]);
        Assert.Equal("openid profile", form.Fields["scope"]);
        Assert.Contains("__RequestVerificationToken", form.Fields.Keys);

        using var response = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/connect/authorize?", location);
        var query = QueryHelpers.ParseQuery(new Uri("https://issuer.example" + location).Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("web-client", query["client_id"].ToString());
        Assert.Equal(RedirectUri, query["redirect_uri"].ToString());
        Assert.Equal("openid profile", query["scope"].ToString());
        Assert.Equal("state-<1>", query["state"].ToString());
        Assert.Equal("nonce-1", query["nonce"].ToString());
        Assert.Equal("120", query["max_age"].ToString());
        Assert.Equal(PkceChallenge, query["code_challenge"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        // prompt=consent has been satisfied and must not be sent again.
        Assert.False(query.ContainsKey("prompt"));
    }

    [Fact]
    public async Task Deny_redirects_to_the_client_with_access_denied_and_state()
    {
        using var client = CreateClient();
        var page = await GetConsentPageAsync(client, "query");
        var form = FindForm(page, "Deny");
        Assert.Equal("web-client", form.Fields["client_id"]);

        using var response = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(RedirectUri, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("state-<1>", query["state"].ToString());
    }

    [Fact]
    public async Task Deny_honors_form_post_response_mode()
    {
        using var client = CreateClient();
        var page = await GetConsentPageAsync(client, "form_post");
        var form = FindForm(page, "Deny");

        using var response = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"action=\"{RedirectUri}\"", html);
        Assert.Contains("name=\"error\" value=\"access_denied\"", html);
        Assert.Contains("name=\"state\" value=\"state-&lt;1&gt;\"", html);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        using var client = CreateClient();
        var page = await GetConsentPageAsync(client, "query");
        var form = FindForm(page, "Approve");
        form.Fields.Remove("__RequestVerificationToken");

        using var response = await client.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private const string PkceChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(TestUserHeader, "6a0d7f4e-4a64-4b39-9f3a-0d4c1f7b9e11");
        return client;
    }

    private static async Task<string> GetConsentPageAsync(HttpClient client, string responseMode)
    {
        var url = QueryHelpers.AddQueryString("/consent", new Dictionary<string, string?>
        {
            ["client_id"] = "web-client",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile",
            ["state"] = "state-<1>",
            ["nonce"] = "nonce-1",
            ["prompt"] = "consent",
            ["max_age"] = "120",
            ["response_mode"] = responseMode,
            ["code_challenge"] = PkceChallenge,
            ["code_challenge_method"] = "S256",
        });
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("class=\"error\"", page);
        return page;
    }

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

    private sealed record ConsentForm(string Action, Dictionary<string, string> Fields);

    /// <summary>
    /// Supplies an authenticated principal for requests carrying the test header, standing in for the
    /// SSO cookie (whose validation needs the database).
    /// </summary>
    private sealed class TestUserStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestUserHeader, out var userId))
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "consent-user"),
                        new Claim("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
                    ], "Test"));
                }

                await nextMiddleware();
            });
            next(app);
        };
    }
}
