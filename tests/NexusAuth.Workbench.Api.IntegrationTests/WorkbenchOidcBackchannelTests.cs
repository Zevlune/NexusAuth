using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchOidcBackchannelTests
{
    [Fact]
    public async Task Revocation_uses_discovered_endpoint_encoded_basic_credentials_and_correct_form()
    {
        using var recorder = new ProviderHandler();
        using var services = CreateProvider(recorder);
        using var scope = services.CreateScope();
        var oidc = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();
        await oidc.RevokeTokenAsync("refresh+secret&value", "refresh_token", CancellationToken.None);
        var request = Assert.Single(recorder.Revocations);
        Assert.Equal("https://issuer.example.test/custom/revoke", request.Url);
        var form = QueryHelpers.ParseQuery("?" + request.Body);
        Assert.Equal("refresh+secret&value", form["token"].ToString());
        Assert.Equal("refresh_token", form["token_type_hint"].ToString());
        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(request.Authorization!["Basic ".Length..]));
        Assert.Equal("workbench:secret%2Bvalue", credentials);
    }

    [Fact]
    public async Task Discovery_is_shared_between_scopes_and_concurrent_login_requests()
    {
        using var recorder = new ProviderHandler();
        using var services = CreateProvider(recorder);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>().FetchDiscoveryAsync(CancellationToken.None);
        }));
        Assert.Equal(1, recorder.DiscoveryRequests);
    }

    [Theory]
    [InlineData("http://issuer.example.test/custom/revoke", "https://issuer.example.test")]
    [InlineData("https://issuer.example.test/custom/revoke", "https://other.example.test")]
    [InlineData("https://user:password@issuer.example.test/custom/revoke", "https://issuer.example.test")]
    public async Task Invalid_provider_metadata_cannot_receive_token_credentials(string endpoint, string issuer)
    {
        using var recorder = new ProviderHandler { RevocationEndpoint = endpoint, Issuer = issuer };
        using var services = CreateProvider(recorder);
        using var scope = services.CreateScope();
        await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>()
            .RevokeTokenAsync("private-token", "refresh_token", CancellationToken.None));
        Assert.Empty(recorder.Revocations);
    }

    [Fact]
    public async Task Provider_failure_is_reported_to_the_logout_caller()
    {
        using var recorder = new ProviderHandler { RevocationStatus = HttpStatusCode.ServiceUnavailable };
        using var services = CreateProvider(recorder);
        using var scope = services.CreateScope();
        await Assert.ThrowsAsync<HttpRequestException>(() => scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>()
            .RevokeTokenAsync("refresh", "refresh_token", CancellationToken.None));
    }

    private static ServiceProvider CreateProvider(ProviderHandler handler)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = "https://issuer.example.test";
            options.ClientId = "workbench";
            options.ClientSecret = "secret+value";
            options.RedirectUri = "https://dashboard.example.test/signin-oidc";
            options.PostLogoutRedirectUri = "https://dashboard.example.test/";
            options.Scope = "openid profile";
        });
        services.AddHttpClient(OidcWorkbenchService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public int DiscoveryRequests;
        public string Issuer = "https://issuer.example.test";
        public string RevocationEndpoint = "https://issuer.example.test/custom/revoke";
        public HttpStatusCode RevocationStatus = HttpStatusCode.OK;
        public List<(string Url, string Body, string? Authorization)> Revocations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref DiscoveryRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        issuer = Issuer, revocation_endpoint = RevocationEndpoint,
                    }), Encoding.UTF8, "application/json"),
                };
            }
            Revocations.Add((request.RequestUri.ToString(), await request.Content!.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(RevocationStatus) { Content = new StringContent(string.Empty) };
        }
    }
}
