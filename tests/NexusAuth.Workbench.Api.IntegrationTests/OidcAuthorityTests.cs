using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OidcAuthorityTests
{
    private const string Authority = "https://login.example.test/identity";
    private const string ExpectedBasicCredentials = "d29ya2JlbmNoOmNsaWVudC1zZWNyZXQ=";

    [Fact]
    public async Task Discovery_uses_configured_authority_and_preserves_original_endpoint_addresses()
    {
        var recorder = new RequestRecorder(DiscoveryJson);
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();

        var discovery = await service.FetchDiscoveryAsync(CancellationToken.None);

        Assert.Equal(Authority, service.Authority);
        Assert.Equal($"{Authority}/.well-known/openid-configuration", Assert.Single(recorder.Requests).RequestUri);
        Assert.Equal(Authority, discovery.Issuer);
        Assert.Equal($"{Authority}/connect/authorize", discovery.AuthorizationEndpoint);
        Assert.Equal($"{Authority}/connect/token", discovery.TokenEndpoint);
        Assert.Equal($"{Authority}/.well-known/jwks.json", discovery.JwksUri);
        Assert.Equal($"{Authority}/connect/introspect", discovery.IntrospectionEndpoint);
    }

    [Fact]
    public async Task Authorization_code_exchange_uses_authority_token_endpoint_and_client_secret_basic()
    {
        var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"access_token":"access","refresh_token":"refresh","id_token":"id","expires_in":120}""");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();

        var result = await service.ExchangeCodeAsync("authorization-code", "code-verifier", CancellationToken.None);

        Assert.Equal("access", result.AccessToken);
        Assert.Equal("refresh", result.RefreshToken);
        Assert.Equal("id", result.IdToken);
        Assert.Equal(
            [
                $"{Authority}/.well-known/openid-configuration",
                $"{Authority}/connect/token",
            ],
            recorder.Requests.Select(request => request.RequestUri));
        var tokenRequest = recorder.Requests[1];
        Assert.Equal("POST", tokenRequest.Method);
        Assert.Equal($"Basic {ExpectedBasicCredentials}", tokenRequest.Authorization);
    }

    [Fact]
    public async Task Introspection_uses_authority_introspection_endpoint_and_client_secret_basic()
    {
        var recorder = new RequestRecorder(
            DiscoveryJson,
            """{"active":true,"sub":"user-1","client_id":"workbench"}""");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();

        var result = await service.IntrospectAccessTokenAsync("access-token", CancellationToken.None);

        Assert.True(result.Active);
        Assert.Equal("user-1", result.Subject);
        Assert.Equal("workbench", result.ClientId);
        Assert.Equal(
            [
                $"{Authority}/.well-known/openid-configuration",
                $"{Authority}/connect/introspect",
            ],
            recorder.Requests.Select(request => request.RequestUri));
        var introspectionRequest = recorder.Requests[1];
        Assert.Equal("POST", introspectionRequest.Method);
        Assert.Equal($"Basic {ExpectedBasicCredentials}", introspectionRequest.Authorization);
    }

    [Fact]
    public async Task Id_token_validation_fetches_original_authority_jwks_uri()
    {
        var recorder = new RequestRecorder(DiscoveryJson, "{}");
        using var provider = CreateProvider(recorder);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOidcWorkbenchService>();
        var discovery = await service.FetchDiscoveryAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => service.ValidateIdTokenAsync(
            discovery,
            "not.a.valid.jwt",
            "expected-nonce",
            CancellationToken.None));

        Assert.Equal(
            [
                $"{Authority}/.well-known/openid-configuration",
                $"{Authority}/.well-known/jwks.json",
            ],
            recorder.Requests.Select(request => request.RequestUri));
        Assert.All(recorder.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Theory]
    [InlineData("ftp://login.example.test/identity")]
    [InlineData("/identity")]
    [InlineData("https://user@login.example.test/identity")]
    [InlineData("https://login.example.test/identity?tenant=workbench")]
    [InlineData("https://login.example.test/identity#fragment")]
    public void Authority_must_be_an_absolute_http_base_uri(string authority)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => CreateValidatedOptions(authority));

        Assert.Contains("Authority must be an absolute HTTP(S) base URI", exception.Message);
    }

    private static ServiceProvider CreateProvider(RequestRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = Authority;
            options.ClientId = "workbench";
            options.ClientSecret = "client-secret";
            options.RedirectUri = "http://localhost:5560/signin-oidc";
            options.PostLogoutRedirectUri = "http://localhost:5560/";
            options.Scope = OidcScopes.OpenId + " " + OidcScopes.Profile;
        }).UseInMemory();
        services.AddHttpClient(OidcWorkbenchService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => recorder);
        return services.BuildServiceProvider();
    }

    private static WorkbenchAuthOptions CreateValidatedOptions(string authority)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = authority;
            options.ClientId = "workbench";
            options.RedirectUri = "http://localhost:5560/signin-oidc";
            options.PostLogoutRedirectUri = "http://localhost:5560/";
            options.Scope = OidcScopes.OpenId + " " + OidcScopes.Profile;
        }).UseInMemory();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<WorkbenchAuthOptions>>().Value;
    }

    private sealed class RequestRecorder(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(
            responseBodies.Length == 0 ? new[] { "{}" } : responseBodies);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record RecordedRequest(string Method, string RequestUri, string? Authorization);

    private const string DiscoveryJson = """
        {
          "issuer":"https://login.example.test/identity",
          "authorization_endpoint":"https://login.example.test/identity/connect/authorize",
          "token_endpoint":"https://login.example.test/identity/connect/token",
          "jwks_uri":"https://login.example.test/identity/.well-known/jwks.json",
          "introspection_endpoint":"https://login.example.test/identity/connect/introspect"
        }
        """;
}
