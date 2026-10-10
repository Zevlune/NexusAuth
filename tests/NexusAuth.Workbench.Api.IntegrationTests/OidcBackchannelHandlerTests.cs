using System.Net;
using System.Text;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OidcBackchannelHandlerTests
{
    private const string PublicAuthority = "https://login.example.test/identity";
    private const string BackchannelAuthority = "http://sso:8080/provider";

    [Theory]
    [InlineData("/.well-known/openid-configuration?cache=0")]
    [InlineData("/.well-known/jwks.json?key=active")]
    [InlineData("/connect/token?client=workbench")]
    [InlineData("/connect/introspect?client=workbench")]
    public async Task Matching_provider_requests_map_to_backchannel_and_preserve_path_and_query(string suffix)
    {
        var recorder = new RequestRecorder();
        using var client = new HttpClient(new OidcBackchannelHandler(PublicAuthority, BackchannelAuthority)
        {
            InnerHandler = recorder,
        });

        using var response = await client.GetAsync($"{PublicAuthority}{suffix}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{BackchannelAuthority}{suffix}", recorder.RequestUri?.ToString());
    }

    [Theory]
    [InlineData("https://other.example.test/identity/connect/token?x=1")]
    [InlineData("https://login.example.test/identity-other/connect/token?x=1")]
    [InlineData("http://login.example.test/identity/connect/token?x=1")]
    public async Task Requests_outside_authority_origin_or_path_prefix_pass_through_unchanged(string requestUri)
    {
        var recorder = new RequestRecorder();
        using var client = new HttpClient(new OidcBackchannelHandler(PublicAuthority, BackchannelAuthority)
        {
            InnerHandler = recorder,
        });

        using var response = await client.GetAsync(requestUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(requestUri, recorder.RequestUri?.ToString());
    }

    [Fact]
    public async Task Fetch_discovery_uses_backchannel_but_keeps_public_issuer_and_authority()
    {
        const string discoveryJson = """
            {
              "issuer":"https://login.example.test/identity",
              "authorization_endpoint":"https://login.example.test/identity/connect/authorize",
              "token_endpoint":"https://login.example.test/identity/connect/token",
              "jwks_uri":"https://login.example.test/identity/.well-known/jwks.json",
              "introspection_endpoint":"https://login.example.test/identity/connect/introspect"
            }
            """;
        var recorder = new RequestRecorder(discoveryJson);
        using var client = CreateHttpClient(recorder);
        var service = CreateService(client);

        var discovery = await service.FetchDiscoveryAsync(CancellationToken.None);

        Assert.Equal($"{BackchannelAuthority}/.well-known/openid-configuration", recorder.RequestUri?.ToString());
        Assert.Equal(PublicAuthority, service.Authority);
        Assert.Equal(PublicAuthority, discovery.Issuer);
        Assert.Equal($"{PublicAuthority}/connect/authorize", discovery.AuthorizationEndpoint);
        Assert.Equal($"{PublicAuthority}/connect/token", discovery.TokenEndpoint);
        Assert.Equal($"{PublicAuthority}/.well-known/jwks.json", discovery.JwksUri);
        Assert.Equal($"{PublicAuthority}/connect/introspect", discovery.IntrospectionEndpoint);
    }

    [Fact]
    public async Task Authorization_code_exchange_maps_public_token_endpoint_to_backchannel()
    {
        var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"access_token":"access","refresh_token":"refresh","id_token":"id","expires_in":120} """);
        using var client = CreateHttpClient(recorder);
        var service = CreateService(client);

        var result = await service.ExchangeCodeAsync("authorization-code", "code-verifier", CancellationToken.None);

        Assert.Equal("access", result.AccessToken);
        Assert.Equal(new[]
        {
            $"{BackchannelAuthority}/.well-known/openid-configuration",
            $"{BackchannelAuthority}/connect/token",
        }, recorder.RequestUris.Select(uri => uri.ToString()));
    }

    [Fact]
    public async Task Introspection_maps_public_endpoint_to_backchannel()
    {
        var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"active":true,"sub":"user-1","client_id":"workbench"}""");
        using var client = CreateHttpClient(recorder);
        var service = CreateService(client);

        var result = await service.IntrospectAccessTokenAsync("access-token", CancellationToken.None);

        Assert.True(result.Active);
        Assert.Equal("user-1", result.Subject);
        Assert.Equal("workbench", result.ClientId);
        Assert.Equal(new[]
        {
            $"{BackchannelAuthority}/.well-known/openid-configuration",
            $"{BackchannelAuthority}/connect/introspect",
        }, recorder.RequestUris.Select(uri => uri.ToString()));
    }

    [Fact]
    public async Task Id_token_validation_fetches_public_jwks_uri_through_backchannel()
    {
        var recorder = new RequestRecorder("{}");
        using var client = CreateHttpClient(recorder);
        var service = CreateService(client);
        var discovery = new DiscoveryDocument
        {
            Issuer = PublicAuthority,
            JwksUri = $"{PublicAuthority}/.well-known/jwks.json",
        };

        await Assert.ThrowsAnyAsync<Exception>(() => service.ValidateIdTokenAsync(
            discovery,
            "not.a.valid.jwt",
            "expected-nonce",
            CancellationToken.None));

        Assert.Equal($"{BackchannelAuthority}/.well-known/jwks.json", Assert.Single(recorder.RequestUris).ToString());
    }

    [Fact]
    public async Task Unconfigured_backchannel_keeps_existing_public_request_behavior()
    {
        var recorder = new RequestRecorder();
        using var client = new HttpClient(new OidcBackchannelHandler(PublicAuthority, null)
        {
            InnerHandler = recorder,
        });

        using var response = await client.GetAsync($"{PublicAuthority}/connect/token?grant_type=refresh_token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{PublicAuthority}/connect/token?grant_type=refresh_token", recorder.RequestUri?.ToString());
    }

    [Theory]
    [InlineData("ftp://sso:8080/provider")]
    [InlineData("http://sso:8080/provider?query=not-allowed")]
    [InlineData("http://user@sso:8080/provider")]
    public void Backchannel_authority_must_be_an_absolute_http_base_uri(string value)
    {
        Assert.Throws<ArgumentException>(() => OidcBackchannelHandler.ValidateBackchannelAuthority(value));
    }

    private sealed class RequestRecorder : HttpMessageHandler
    {
        private readonly Queue<string> responses;
        public List<Uri> RequestUris { get; } = [];
        public Uri? RequestUri => RequestUris.LastOrDefault();

        public RequestRecorder(params string[] responseBodies)
        {
            responses = new Queue<string>(responseBodies.Length == 0 ? new[] { "{}" } : responseBodies);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpClient CreateHttpClient(RequestRecorder recorder) =>
        new(new OidcBackchannelHandler(PublicAuthority, BackchannelAuthority)
        {
            InnerHandler = recorder,
        });

    private static OidcWorkbenchService CreateService(HttpClient client) => new(
        new SingleClientFactory(client),
        new InMemoryFlowStateStore(),
        Options.Create(new WorkbenchAuthOptions
        {
            Authority = PublicAuthority,
            BackchannelAuthority = BackchannelAuthority,
            ClientId = "workbench",
            ClientSecret = "client-secret",
            RedirectUri = "http://localhost:5560/signin-oidc",
            PostLogoutRedirectUri = "http://localhost:5560/",
            Scope = "openid profile",
        }));

    private const string DiscoveryJson = """
        {
          "issuer":"https://login.example.test/identity",
          "authorization_endpoint":"https://login.example.test/identity/connect/authorize",
          "token_endpoint":"https://login.example.test/identity/connect/token",
          "jwks_uri":"https://login.example.test/identity/.well-known/jwks.json",
          "introspection_endpoint":"https://login.example.test/identity/connect/introspect"
        }
        """;

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
