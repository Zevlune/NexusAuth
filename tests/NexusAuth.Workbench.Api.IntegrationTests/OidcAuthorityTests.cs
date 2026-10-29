using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OidcAuthorityTests
{
    private const string Authority = "https://login.example.test/identity";
    private const string ExpectedBasicCredentials = "d29ya2JlbmNoOmNsaWVudC1zZWNyZXQ=";

    [Fact]
    public async Task Discovery_uses_configured_authority_and_returns_standard_configuration()
    {
        using var recorder = new RequestRecorder(DiscoveryJson);
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INexusAuthClient>();

        var discovery = await client.FetchConfigurationAsync(CancellationToken.None);

        Assert.Equal(
            $"{Authority}/.well-known/openid-configuration",
            Assert.Single(recorder.Requests).RequestUri);
        Assert.Equal(Authority, discovery.Issuer);
        Assert.Equal($"{Authority}/connect/authorize", discovery.AuthorizationEndpoint);
        Assert.Equal($"{Authority}/connect/token", discovery.TokenEndpoint);
        Assert.Equal($"{Authority}/connect/introspect", discovery.IntrospectionEndpoint);
    }

    [Fact]
    public async Task Authorization_code_exchange_uses_discovered_endpoint_and_basic_authentication()
    {
        using var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"access_token":"access","refresh_token":"refresh","id_token":"id","expires_in":120}""");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INexusAuthClient>();

        var result = await client.ExchangeCodeAsync(
            "authorization-code",
            "code-verifier",
            "https://app.example.test/signin-oidc",
            CancellationToken.None);

        Assert.Equal("access", result.AccessToken);
        Assert.Equal("refresh", result.RefreshToken);
        Assert.Equal("id", result.IdToken);
        Assert.Equal(120, result.ExpiresIn);
        Assert.Equal(
            [
                $"{Authority}/.well-known/openid-configuration",
                $"{Authority}/connect/token"
            ],
            recorder.Requests.Select(request => request.RequestUri));
        var tokenRequest = recorder.Requests[1];
        Assert.Equal("POST", tokenRequest.Method);
        Assert.Equal($"Basic {ExpectedBasicCredentials}", tokenRequest.Authorization);
    }

    [Fact]
    public async Task Introspection_uses_discovered_endpoint_and_basic_authentication()
    {
        using var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"active":true,"sub":"user-1","client_id":"workbench"}""");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INexusAuthClient>();

        var result = await client.IntrospectAccessTokenAsync(
            "access-token",
            CancellationToken.None);

        Assert.True(result.Active);
        Assert.Equal("user-1", result.Subject);
        Assert.Equal("workbench", result.ClientId);
        Assert.Equal(
            [
                $"{Authority}/.well-known/openid-configuration",
                $"{Authority}/connect/introspect"
            ],
            recorder.Requests.Select(request => request.RequestUri));
        var introspectionRequest = recorder.Requests[1];
        Assert.Equal("POST", introspectionRequest.Method);
        Assert.Equal($"Basic {ExpectedBasicCredentials}", introspectionRequest.Authorization);
    }

    [Fact]
    public async Task Discovery_issuer_must_exactly_equal_authority()
    {
        using var recorder = new RequestRecorder("""
            {
              "issuer":"https://other.example.test/identity",
              "token_endpoint":"https://other.example.test/identity/connect/token"
            }
            """);
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
                .FetchConfigurationAsync(CancellationToken.None));

        Assert.Contains("issuer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(recorder.Requests);
    }

    [Theory]
    [InlineData("ftp://login.example.test/identity")]
    [InlineData("/identity")]
    [InlineData("https://user@login.example.test/identity")]
    [InlineData("https://login.example.test/identity?tenant=workbench")]
    [InlineData("https://login.example.test/identity#fragment")]
    [InlineData("http://login.example.test/identity")]
    public void Authority_must_be_absolute_https_without_user_info_query_or_fragment(string authority)
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            CreateValidatedOptions(authority, requireHttps: true));

        Assert.Contains("Authority must be", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireHttpsMetadata_false_explicitly_allows_http()
    {
        const string httpAuthority = "http://login.example.test/identity";
        using var recorder = new RequestRecorder($$"""
            {
              "issuer":"{{httpAuthority}}",
              "token_endpoint":"{{httpAuthority}}/connect/token"
            }
            """);
        using var provider = CreateProvider(
            recorder,
            authority: httpAuthority,
            requireHttpsMetadata: false);
        using var scope = provider.CreateScope();

        var discovery = await scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
            .FetchConfigurationAsync(CancellationToken.None);

        Assert.Equal(httpAuthority, discovery.Issuer);
        Assert.Equal(
            $"{httpAuthority}/.well-known/openid-configuration",
            Assert.Single(recorder.Requests).RequestUri);
    }

    [Fact]
    public async Task Fetch_configuration_honors_cancellation_even_if_manager_ignores_it()
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = Authority;
            options.ClientId = "workbench";
            options.ClientSecret = "client-secret";
        });
        services.RemoveAll<IConfigurationManager<OpenIdConnectConfiguration>>();
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(
            new NeverCompletingConfigurationManager());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
                .FetchConfigurationAsync(cancellation.Token));
    }

    [Fact]
    public async Task Non_object_discovery_json_becomes_protocol_exception()
    {
        using var recorder = new RequestRecorder("[]");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<NexusAuthProtocolException>(() =>
            scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
                .FetchConfigurationAsync(CancellationToken.None));

        Assert.Equal("invalid_response", exception.Error);
    }

    [Fact]
    public async Task Discovery_http_error_becomes_protocol_exception_with_status_and_error_code()
    {
        using var recorder = new RequestRecorder("""{"error":"temporarily_unavailable"}""")
        {
            ResponseStatusCode = HttpStatusCode.BadGateway
        };
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<NexusAuthProtocolException>(() =>
            scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
                .FetchConfigurationAsync(CancellationToken.None));

        Assert.Equal("temporarily_unavailable", exception.Error);
        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [Fact]
    public void Named_http_client_has_thirty_second_default_timeout()
    {
        using var recorder = new RequestRecorder(DiscoveryJson);
        using var provider = CreateProvider(recorder);
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(NexusAuthClient.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    private static ServiceProvider CreateProvider(
        RequestRecorder recorder,
        string authority = Authority,
        bool requireHttpsMetadata = true)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = authority;
            options.ClientId = "workbench";
            options.ClientSecret = "client-secret";
            options.RequireHttpsMetadata = requireHttpsMetadata;
        });
        services.AddHttpClient(NexusAuthClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => recorder);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static NexusAuthClientOptions CreateValidatedOptions(
        string authority,
        bool requireHttps)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = authority;
            options.ClientId = "workbench";
            options.ClientSecret = "client-secret";
            options.RequireHttpsMetadata = requireHttps;
        });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value;
    }

    private sealed class RequestRecorder(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(
            responseBodies.Length == 0 ? ["{}"] : responseBodies);

        public List<RecordedRequest> Requests { get; } = [];
        public HttpStatusCode ResponseStatusCode { get; init; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record RecordedRequest(string Method, string RequestUri, string? Authorization);

    private sealed class NeverCompletingConfigurationManager
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly TaskCompletionSource<OpenIdConnectConfiguration> _configuration =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            _configuration.Task;

        public void RequestRefresh()
        {
        }
    }

    private const string DiscoveryJson = """
        {
          "issuer":"https://login.example.test/identity",
          "authorization_endpoint":"https://login.example.test/identity/connect/authorize",
          "token_endpoint":"https://login.example.test/identity/connect/token",
          "introspection_endpoint":"https://login.example.test/identity/connect/introspect"
        }
        """;
}
