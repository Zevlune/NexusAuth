using NexusAuth.Application.Services.Tokens;
using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Authentication.Tokens;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchOidcBackchannelTests
{
    [Fact]
    public async Task Revocation_uses_discovered_endpoint_encoded_basic_credentials_and_correct_form()
    {
        using var handler = new ProviderHandler();
        using var services = CreateProvider(handler);
        using var scope = services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>();

        await client.RevokeTokenAsync("refresh+secret&value", "refresh_token", CancellationToken.None);

        var request = Assert.Single(handler.Revocations);
        Assert.Equal("https://issuer.example.test/custom/revoke", request.Url);
        var form = QueryHelpers.ParseQuery("?" + request.Body);
        Assert.Equal("refresh+secret&value", form["token"].ToString());
        Assert.Equal("refresh_token", form["token_type_hint"].ToString());
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));
        var credentials = Encoding.UTF8.GetString(
            Convert.FromBase64String(request.Authorization!["Basic ".Length..]));
        Assert.Equal("workbench:secret%2Bvalue", credentials);
    }

    [Fact]
    public async Task Configuration_manager_and_jwks_are_shared_across_scopes()
    {
        using var handler = new ProviderHandler();
        using var services = CreateProvider(handler);
        var oidcOptions = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
        var manager = oidcOptions.ConfigurationManager!;

        var introspections = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>()
                .IntrospectAccessTokenAsync("access", CancellationToken.None);
        }));

        Assert.Equal(1, handler.DiscoveryRequests);
        Assert.Equal(1, handler.JwksRequests);
        Assert.Equal(12, handler.IntrospectionRequests);
        Assert.All(introspections, result => Assert.True(result.Active));
    }

    [Theory]
    [InlineData("http://issuer.example.test/custom/revoke", "https://issuer.example.test")]
    [InlineData("https://issuer.example.test/custom/revoke", "https://other.example.test")]
    [InlineData("https://user:password@issuer.example.test/custom/revoke", "https://issuer.example.test")]
    [InlineData("https://issuer.example.test/custom/revoke#fragment", "https://issuer.example.test")]
    public async Task Invalid_provider_metadata_cannot_receive_token_credentials(
        string endpoint,
        string issuer)
    {
        using var handler = new ProviderHandler
        {
            RevocationEndpoint = endpoint,
            Issuer = issuer
        };
        using var services = CreateProvider(handler);
        using var scope = services.CreateScope();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>()
                .RevokeTokenAsync("private-token", "refresh_token", CancellationToken.None));

        Assert.Empty(handler.Revocations);
    }

    [Fact]
    public async Task OAuth_error_code_is_preserved_without_exposing_response_or_secret()
    {
        const string secret = "secret+value";
        const string description = "provider reflected secret+value";
        using var handler = new ProviderHandler
        {
            RevocationStatus = HttpStatusCode.BadRequest,
            RevocationError = "invalid_token",
            RevocationErrorDescription = description
        };
        using var services = CreateProvider(handler);
        using var scope = services.CreateScope();

        var exception = await Assert.ThrowsAsync<WorkbenchProtocolException>(() =>
            scope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>()
                .RevokeTokenAsync("refresh", "refresh_token", CancellationToken.None));

        Assert.Equal("invalid_token", exception.Error);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(description, exception.ToString(), StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(ProviderHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = "https://issuer.example.test",
                ["Auth:Audience"] = "workbench.api",
                ["Auth:ClientId"] = "workbench",
                ["Auth:ClientSecret"] = "secret+value",
                ["Auth:RedirectUri"] = "https://app.test/signin-oidc",
                ["Auth:PostLogoutRedirectUri"] = "https://app.test/",
                ["Auth:Scope"] = "openid profile offline_access"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IUserTokenValidator>(_ =>
            RecordingDispatchProxy.Create<IUserTokenValidator>((_, _) => false));
        services.AddWorkbenchAuthentication(configuration);
        services.AddHttpClient(WorkbenchTokenClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private sealed class ProviderHandler : HttpMessageHandler
    {
        private readonly object revocationGate = new();

        public int DiscoveryRequests;
        public int JwksRequests;
        public int IntrospectionRequests;
        public string Issuer { get; init; } = "https://issuer.example.test";
        public string RevocationEndpoint { get; init; } =
            "https://issuer.example.test/custom/revoke";
        public HttpStatusCode RevocationStatus { get; init; } = HttpStatusCode.OK;
        public string? RevocationError { get; init; }
        public string? RevocationErrorDescription { get; init; }
        public List<(string Url, string Body, string? Authorization)> Revocations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref DiscoveryRequests);
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    issuer = Issuer,
                    jwks_uri = "https://issuer.example.test/.well-known/jwks.json",
                    introspection_endpoint = "https://issuer.example.test/custom/introspect",
                    revocation_endpoint = RevocationEndpoint
                }));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("jwks.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref JwksRequests);
                return JsonResponse("""{"keys":[]}""");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/custom/introspect", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref IntrospectionRequests);
                return JsonResponse("""{"active":true,"sub":"user-1","client_id":"workbench"}""");
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct);
            lock (revocationGate)
            {
                Revocations.Add((
                    request.RequestUri.ToString(),
                    body,
                    request.Headers.Authorization?.ToString()));
            }

            return RevocationStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : JsonResponse(JsonSerializer.Serialize(new
                {
                    error = RevocationError,
                    error_description = RevocationErrorDescription
                }), RevocationStatus);
        }

        private static HttpResponseMessage JsonResponse(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
