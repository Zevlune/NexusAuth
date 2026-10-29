using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Clients;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchClientAuthenticationTests
{
    private const string Authority = "https://login.example.test/identity";
    private const string ExpectedClientId = "client+id%: 用户";
    private const string ExpectedClientSecret = "secret+value%: 空格🔑";

    [Theory]
    [InlineData("exchange")]
    [InlineData("refresh")]
    [InlineData("introspection")]
    public async Task Basic_credentials_are_form_encoded_before_base64_and_roundtrip_through_host_parser(
        string operation)
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(handler);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<INexusAuthClient>();

        switch (operation)
        {
            case "exchange":
                await client.ExchangeCodeAsync(
                    "authorization-code",
                    "code-verifier",
                    "https://app.test/signin-oidc",
                    CancellationToken.None);
                break;
            case "refresh":
                await client.RefreshTokensAsync("refresh-token", CancellationToken.None);
                break;
            case "introspection":
                await client.IntrospectAccessTokenAsync("access-token", CancellationToken.None);
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
    public async Task Missing_refresh_token_is_preserved_as_null_and_expires_in_is_not_defaulted()
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(handler);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
            .ExchangeCodeAsync(
                "authorization-code",
                "code-verifier",
                "https://app.test/signin-oidc",
                CancellationToken.None);

        Assert.Equal("access", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Null(result.IdToken);
        Assert.Equal(0, result.ExpiresIn);
    }

    [Fact]
    public async Task Basic_authentication_does_not_trim_sdk_client_secret()
    {
        const string secret = " \tsecret+value\n ";
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(handler, clientSecret: secret);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
            .RefreshTokensAsync("refresh-token", CancellationToken.None);

        var authorization = Assert.Single(handler.AuthorizationHeaders);
        var credentials = Encoding.UTF8.GetString(
            Convert.FromBase64String(Assert.IsType<string>(authorization.Parameter)));
        Assert.Equal(
            $"{WebUtility.UrlEncode(ExpectedClientId)}:{WebUtility.UrlEncode(secret)}",
            credentials);
    }

    [Fact]
    public async Task Client_secret_post_sends_credentials_in_form_without_basic_header()
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(
            handler,
            clientSecret: "post+secret",
            ClientAuthenticationMethod.ClientSecretPost);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
            .RefreshTokensAsync("refresh-token", CancellationToken.None);

        Assert.Empty(handler.AuthorizationHeaders);
        var request = Assert.Single(handler.ProtectedRequests);
        var form = QueryHelpers.ParseQuery("?" + request.Body);
        Assert.Equal(ExpectedClientId, form["client_id"].ToString());
        Assert.Equal("post+secret", form["client_secret"].ToString());
    }

    [Fact]
    public async Task Public_client_sends_only_client_id_and_does_not_require_secret()
    {
        using var handler = new CapturingHandler();
        using var provider = CreateProvider(
            handler,
            clientSecret: null,
            ClientAuthenticationMethod.None);
        using var scope = provider.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value;
        Assert.Null(options.ClientSecret);
        await scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
            .RefreshTokensAsync("refresh-token", CancellationToken.None);

        Assert.Empty(handler.AuthorizationHeaders);
        var request = Assert.Single(handler.ProtectedRequests);
        var form = QueryHelpers.ParseQuery("?" + request.Body);
        Assert.Equal(ExpectedClientId, form["client_id"].ToString());
        Assert.False(form.ContainsKey("client_secret"));
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

        var exception = await Assert.ThrowsAsync<NexusAuthProtocolException>(() =>
            scope.ServiceProvider.GetRequiredService<INexusAuthClient>()
                .RefreshTokensAsync("refresh-token", CancellationToken.None));

        Assert.Equal("invalid_response", exception.Error);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
    }

    private static ServiceProvider CreateProvider(
        CapturingHandler handler,
        string? clientSecret = ExpectedClientSecret,
        ClientAuthenticationMethod authenticationMethod = ClientAuthenticationMethod.ClientSecretBasic)
    {
        var services = new ServiceCollection();
        services.AddNexusAuth(options =>
        {
            options.Authority = Authority;
            options.ClientId = ExpectedClientId;
            options.ClientSecret = clientSecret;
            options.ClientAuthenticationMethod = authenticationMethod;
        });
        services.AddHttpClient(NexusAuthClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string TokenResponseBody { get; init; } = """{"access_token":"access"}""";
        public List<AuthenticationHeaderValue> AuthorizationHeaders { get; } = [];
        public List<RecordedRequest> ProtectedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
                return JsonResponse(DiscoveryJson);

            if (request.Headers.Authorization is not null)
                AuthorizationHeaders.Add(request.Headers.Authorization);

            var requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            ProtectedRequests.Add(new RecordedRequest(request.RequestUri.ToString(), requestBody));

            if (request.RequestUri.AbsolutePath.EndsWith("/connect/introspect", StringComparison.Ordinal))
                return JsonResponse("""{"active":true,"sub":"user-1","client_id":"client-id"}""");

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

    private sealed record RecordedRequest(string Url, string Body);

    private const string DiscoveryJson = """
        {
          "issuer":"https://login.example.test/identity",
          "token_endpoint":"https://login.example.test/identity/connect/token",
          "introspection_endpoint":"https://login.example.test/identity/connect/introspect"
        }
        """;
}
