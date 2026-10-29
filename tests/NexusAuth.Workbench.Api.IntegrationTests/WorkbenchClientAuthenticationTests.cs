using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Authentication.Tokens;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Application.Clients;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchClientAuthenticationTests
{
    private const string Authority = "https://login.example.test/identity";
    private const string ExpectedClientId = "client+id%: 用户";
    private const string ExpectedClientSecret = "secret+value%: 空格🔑";

    [Theory]
    [InlineData("refresh")]
    [InlineData("introspection")]
    [InlineData("revocation")]
    public async Task Basic_credentials_are_form_encoded_before_base64_and_roundtrip_through_host_parser(
        string operation)
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(handler);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>();

        switch (operation)
        {
            case "refresh":
                await client.RefreshTokensAsync("refresh-token", CancellationToken.None);
                break;
            case "introspection":
                await client.IntrospectAccessTokenAsync("access-token", CancellationToken.None);
                break;
            case "revocation":
                await client.RevokeTokenAsync("refresh-token", "refresh_token", CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        var authorization = Assert.Single(handler.AuthorizationHeaders);
        Assert.Equal("Basic", authorization.Scheme);
        var encodedCredentials = Encoding.UTF8.GetString(
            Convert.FromBase64String(Assert.IsType<string>(authorization.Parameter)));
        Assert.Equal(
            $"{WebUtility.UrlEncode(ExpectedClientId)}:{WebUtility.UrlEncode(ExpectedClientSecret)}",
            encodedCredentials);

        var protectedRequest = Assert.Single(handler.ProtectedRequests);
        var form = QueryHelpers.ParseQuery("?" + protectedRequest.Body);
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));

        var parsed = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            authorization.ToString(),
            formClientId: null,
            formClientSecret: null);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(ExpectedClientId, parsed.Authentication!.ClientId);
        Assert.Equal(ExpectedClientSecret, parsed.Authentication.ClientSecret);
        Assert.Equal(
            OAuthClientAuthenticationMethods.ClientSecretBasic,
            parsed.Authentication.TokenEndpointAuthMethod);
    }

    [Fact]
    public async Task Native_code_exchange_form_is_converted_to_basic_without_duplicate_credentials()
    {
        using var terminal = new CapturingHandler();
        using var provider = CreateOpenIdConnectProvider(terminal);
        using var httpClient = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("oidc-backchannel");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Authority}/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = OpenIdConnectGrantTypes.AuthorizationCode,
                ["code"] = "authorization-code",
                ["redirect_uri"] = "https://app.test/signin-oidc",
                ["code_verifier"] = "code-verifier",
                ["client_id"] = ExpectedClientId,
                ["client_secret"] = ExpectedClientSecret
            })
        };

        using var response = await httpClient.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var protectedRequest = Assert.Single(terminal.ProtectedRequests);
        var form = QueryHelpers.ParseQuery("?" + protectedRequest.Body);
        Assert.Equal(OpenIdConnectGrantTypes.AuthorizationCode, form["grant_type"].ToString());
        Assert.Equal("authorization-code", form["code"].ToString());
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));

        var authorization = Assert.Single(terminal.AuthorizationHeaders);
        var credentials = Encoding.UTF8.GetString(
            Convert.FromBase64String(Assert.IsType<string>(authorization.Parameter)));
        Assert.Equal(
            $"{WebUtility.UrlEncode(ExpectedClientId)}:{WebUtility.UrlEncode(ExpectedClientSecret)}",
            credentials);
    }

    [Fact]
    public async Task Discovery_get_never_sends_client_credentials()
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(handler);
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
            .ConfigurationManager!;

        await manager.GetConfigurationAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Null(request.Authorization);
        Assert.Empty(request.Body);
        Assert.Empty(handler.AuthorizationHeaders);
        Assert.Empty(handler.ProtectedRequests);
    }

    [Fact]
    public async Task Missing_refresh_token_is_preserved_as_null_and_expires_in_is_not_defaulted()
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(handler);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>()
            .RefreshTokensAsync("refresh-token", CancellationToken.None);

        Assert.Equal("access", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.IdToken);
        Assert.Equal(0, result.ExpiresIn);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"access_token":"access","expires_in":"invalid"}""")]
    [InlineData("""{"access_token":"access","expires_in":-1}""")]
    public async Task Invalid_token_response_shapes_become_protocol_exceptions(string responseBody)
    {
        using var handler = new CapturingHandler { TokenResponseBody = responseBody };
        using var provider = CreateProvider(handler);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<WorkbenchProtocolException>(() =>
            scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>()
                .RefreshTokensAsync("refresh-token", CancellationToken.None));

        Assert.Equal("invalid_response", exception.Error);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.DoesNotContain(ExpectedClientSecret, exception.ToString(), StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(CapturingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkbenchAuthentication(CreateConfiguration());
        services.AddHttpClient(WorkbenchTokenClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static ServiceProvider CreateOpenIdConnectProvider(CapturingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddOpenIdConnect(WorkbenchAuthenticationDefaults.OpenIdConnectScheme, options =>
            {
                options.Authority = Authority;
                options.ClientId = ExpectedClientId;
                options.ClientSecret = ExpectedClientSecret;
                options.RequireHttpsMetadata = true;
            });
        services.AddHttpClient("oidc-backchannel")
            .UseOpenIdConnectClientSecretBasic(
                WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = Authority,
                ["Auth:Audience"] = "workbench.api",
                ["Auth:ClientId"] = ExpectedClientId,
                ["Auth:ClientSecret"] = ExpectedClientSecret,
                ["Auth:RedirectUri"] = "https://app.test/signin-oidc",
                ["Auth:PostLogoutRedirectUri"] = "https://app.test/",
                ["Auth:Scope"] = "openid profile offline_access"
            })
            .Build();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string TokenResponseBody { get; init; } = """{"access_token":"access"}""";
        public List<AuthenticationHeaderValue> AuthorizationHeaders { get; } = [];
        public List<RecordedRequest> Requests { get; } = [];
        public List<RecordedRequest> ProtectedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                requestBody));

            if (request.RequestUri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
                return JsonResponse(DiscoveryJson);

            if (request.Method == HttpMethod.Post)
            {
                if (request.Headers.Authorization is not null)
                    AuthorizationHeaders.Add(request.Headers.Authorization);
                ProtectedRequests.Add(new RecordedRequest(
                    request.Method.Method,
                    request.RequestUri.ToString(),
                    request.Headers.Authorization?.ToString(),
                    requestBody));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/connect/introspect", StringComparison.Ordinal))
                return JsonResponse("""{"active":true,"sub":"user-1","client_id":"client-id"}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/connect/revoke", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK);

            return JsonResponse(TokenResponseBody);
        }

        private static HttpResponseMessage JsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Url,
        string? Authorization,
        string Body);

    private const string DiscoveryJson = """
        {
          "issuer":"https://login.example.test/identity",
          "token_endpoint":"https://login.example.test/identity/connect/token",
          "introspection_endpoint":"https://login.example.test/identity/connect/introspect",
          "revocation_endpoint":"https://login.example.test/identity/connect/revoke"
        }
        """;
}
