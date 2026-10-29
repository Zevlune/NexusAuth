using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Workbench.Api;
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
        var client = scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>();

        var discovery = await client.FetchConfigurationAsync(CancellationToken.None);

        var request = Assert.Single(recorder.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal($"{Authority}/.well-known/openid-configuration", request.RequestUri);
        Assert.Null(request.Authorization);
        Assert.Empty(request.Body);
        Assert.Equal(Authority, discovery.Issuer);
        Assert.Equal($"{Authority}/connect/authorize", discovery.AuthorizationEndpoint);
        Assert.Equal($"{Authority}/connect/token", discovery.TokenEndpoint);
        Assert.Equal($"{Authority}/connect/introspect", discovery.IntrospectionEndpoint);
    }

    [Fact]
    public async Task Refresh_uses_discovered_endpoint_and_basic_authentication()
    {
        using var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"access_token":"access","refresh_token":"rotated","id_token":"id","expires_in":120}""");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>();

        var result = await client.RefreshTokensAsync("refresh", CancellationToken.None);

        Assert.Equal("access", result.AccessToken);
        Assert.Equal("rotated", result.RefreshToken);
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
        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?" + tokenRequest.Body);
        Assert.Equal(OpenIdConnectGrantTypes.RefreshToken, form["grant_type"].ToString());
        Assert.Equal("refresh", form["refresh_token"].ToString());
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task Introspection_uses_discovered_endpoint_and_basic_authentication()
    {
        using var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"active":true,"sub":"user-1","client_id":"workbench"}""");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>();

        var result = await client.IntrospectAccessTokenAsync("access-token", CancellationToken.None);

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
            scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>()
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
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkbenchAuthenticationSettings.FromConfiguration(CreateConfiguration(authority)));

        Assert.Contains("Auth:Authority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireHttpsMetadata_false_explicitly_allows_http()
    {
        const string httpAuthority = "http://login.example.test/identity";
        using var recorder = new RequestRecorder($$"""
            {
              "issuer":"{{httpAuthority}}",
              "token_endpoint":"{{httpAuthority}}/connect/token",
              "jwks_uri":"{{httpAuthority}}/.well-known/jwks.json"
            }
            """, """{"keys":[]}""");
        using var provider = CreateProvider(
            recorder,
            authority: httpAuthority,
            requireHttpsMetadata: false);
        using var scope = provider.CreateScope();

        var discovery = await scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>()
            .FetchConfigurationAsync(CancellationToken.None);

        Assert.Equal(httpAuthority, discovery.Issuer);
        var discoveryRequest = Assert.Single(
            recorder.Requests, request =>
                request.RequestUri.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal));
        Assert.Equal($"{httpAuthority}/.well-known/openid-configuration", discoveryRequest.RequestUri);
        Assert.Contains(
            recorder.Requests,
            request => request.RequestUri == $"{httpAuthority}/.well-known/jwks.json");
        Assert.All(recorder.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.StartsWith($"{httpAuthority}/", request.RequestUri, StringComparison.Ordinal);
            Assert.Null(request.Authorization);
            Assert.Empty(request.Body);
        });
    }

    [Fact]
    public async Task Fetch_configuration_honors_cancellation_even_if_manager_ignores_it()
    {
        var manager = new NeverCompletingConfigurationManager();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkbenchAuthentication(CreateConfiguration());
        services.PostConfigure<OpenIdConnectOptions>(
            WorkbenchAuthenticationDefaults.OpenIdConnectScheme,
            options => options.ConfigurationManager = manager);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>()
                .FetchConfigurationAsync(cancellation.Token));
    }

    [Fact]
    public async Task Non_object_discovery_json_fails_closed_without_credentials()
    {
        using var recorder = new RequestRecorder("[]");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>()
                .FetchConfigurationAsync(CancellationToken.None));

        Assert.DoesNotContain("client-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(Assert.Single(recorder.Requests).Authorization);
    }

    [Fact]
    public async Task Discovery_http_error_fails_closed_without_exposing_credentials()
    {
        using var recorder = new RequestRecorder(
            """{"error":"temporarily_unavailable","client_secret":"client-secret"}""")
        {
            ResponseStatusCode = HttpStatusCode.BadGateway
        };
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWorkbenchOidcClient>()
                .FetchConfigurationAsync(CancellationToken.None));

        Assert.DoesNotContain("client-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(Assert.Single(recorder.Requests).Authorization);
    }

    [Fact]
    public void Named_http_client_has_thirty_second_default_timeout()
    {
        using var recorder = new RequestRecorder(DiscoveryJson);
        using var provider = CreateProvider(recorder);
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(WorkbenchOidcClient.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    private static ServiceProvider CreateProvider(
        RequestRecorder recorder,
        string authority = Authority,
        bool requireHttpsMetadata = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkbenchAuthentication(CreateConfiguration(authority, requireHttpsMetadata));
        services.AddHttpClient(WorkbenchOidcClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => recorder);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static IConfiguration CreateConfiguration(
        string authority = Authority,
        bool requireHttpsMetadata = true)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = authority,
                ["Auth:Audience"] = "workbench.api",
                ["Auth:ClientId"] = "workbench",
                ["Auth:ClientSecret"] = "client-secret",
                ["Auth:RedirectUri"] = "https://app.test/signin-oidc",
                ["Auth:PostLogoutRedirectUri"] = "https://app.test/",
                ["Auth:Scope"] = "openid profile offline_access",
                ["Auth:RequireHttpsMetadata"] = requireHttpsMetadata.ToString()
            })
            .Build();
    }

    private sealed class RequestRecorder(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Queue<string> responses = new(
            responseBodies.Length == 0 ? ["{}"] : responseBodies);
        private string? lastResponse;

        public List<RecordedRequest> Requests { get; } = [];
        public HttpStatusCode ResponseStatusCode { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            string responseBody;
            lock (gate)
            {
                Requests.Add(new RecordedRequest(
                    request.Method.Method,
                    request.RequestUri!.ToString(),
                    request.Headers.Authorization?.ToString(),
                    body));
                if (responses.Count > 0)
                    lastResponse = responses.Dequeue();
                responseBody = lastResponse ?? "{}";
            }

            return new HttpResponseMessage(ResponseStatusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string RequestUri,
        string? Authorization,
        string Body);

    private sealed class NeverCompletingConfigurationManager
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly TaskCompletionSource<OpenIdConnectConfiguration> configuration =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            configuration.Task;

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
