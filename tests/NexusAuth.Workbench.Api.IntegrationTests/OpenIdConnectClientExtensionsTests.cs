using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OpenIdConnectClientExtensionsTests
{
    private const string FirstScheme = "first-oidc";
    private const string SecondScheme = "second-oidc";
    private const string Authority = "https://login.example.test/identity";

    [Fact]
    public async Task Basic_credentials_are_isolated_by_authentication_scheme()
    {
        var firstHandler = new CapturingHandler();
        var secondHandler = new CapturingHandler();
        using var provider = CreateBackchannelProvider(
            (FirstScheme, "first-client", "first-secret", firstHandler),
            (SecondScheme, "second-client", "second-secret", secondHandler));
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var firstClient = factory.CreateClient(FirstScheme);
        using var firstResponse = await firstClient.SendAsync(
            CreateTokenRequest(), CancellationToken.None);
        using var secondClient = factory.CreateClient(SecondScheme);
        using var secondResponse = await secondClient.SendAsync(
            CreateTokenRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(
            "first-client:first-secret",
            DecodeBasic(Assert.Single(firstHandler.AuthorizationHeaders)));
        Assert.Equal(
            "second-client:second-secret",
            DecodeBasic(Assert.Single(secondHandler.AuthorizationHeaders)));
    }

    [Fact]
    public async Task Basic_credentials_are_form_encoded_and_form_credentials_are_removed()
    {
        const string clientId = "client+id%: 用户";
        const string clientSecret = "secret+value%: 空格🔑";
        var handler = new CapturingHandler();
        using var provider = CreateBackchannelProvider(
            (FirstScheme, clientId, clientSecret, handler));
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(FirstScheme);

        using var response = await client.SendAsync(CreateTokenRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"{WebUtility.UrlEncode(clientId)}:{WebUtility.UrlEncode(clientSecret)}",
            DecodeBasic(Assert.Single(handler.AuthorizationHeaders)));
        var form = QueryHelpers.ParseQuery("?" + request.Body);
        Assert.Equal(OpenIdConnectGrantTypes.AuthorizationCode, form["grant_type"].ToString());
        Assert.Equal("authorization-code", form["code"].ToString());
        Assert.False(form.ContainsKey("client_id"));
        Assert.False(form.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task Metadata_get_uses_the_named_backchannel_without_basic_credentials()
    {
        var handler = new CapturingHandler { DiscoveryJson = CreateDiscoveryJson(Authority) };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddOpenIdConnect(FirstScheme, options =>
            {
                options.Authority = Authority;
                options.ClientId = "metadata-client";
                options.ClientSecret = "metadata-secret";
                options.RequireHttpsMetadata = true;
            });
        services.AddHttpClient(FirstScheme)
            .UseOpenIdConnectClientSecretBasic(FirstScheme)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddOptions<OpenIdConnectOptions>(FirstScheme)
            .Configure<IHttpClientFactory>((options, http) =>
                options.Backchannel = http.CreateClient(FirstScheme));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(FirstScheme)
            .ConfigurationManager!;

        var configuration = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.Equal(Authority, configuration.Issuer);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get.Method, request.Method);
        Assert.EndsWith("/.well-known/openid-configuration", request.Url, StringComparison.Ordinal);
        Assert.Null(request.Authorization);
        Assert.Empty(request.Body);
        Assert.Empty(handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task Metadata_warmup_calls_each_native_oidc_manager_and_skips_other_schemes()
    {
        var firstManager = new TrackingManager();
        var secondManager = new TrackingManager();
        var nonOidcManager = new TrackingManager();
        using var host = CreateHost(services =>
        {
            services.AddLogging();
            services.AddAuthentication()
                .AddCookie("cookie")
                .AddOpenIdConnect(FirstScheme, options => options.ClientId = "first")
                .AddOpenIdConnect(SecondScheme, options => options.ClientId = "second");
            services.PostConfigure<OpenIdConnectOptions>(
                FirstScheme, options => options.ConfigurationManager = firstManager);
            services.PostConfigure<OpenIdConnectOptions>(
                SecondScheme, options => options.ConfigurationManager = secondManager);
            services.PostConfigure<OpenIdConnectOptions>(
                "cookie", options => options.ConfigurationManager = nonOidcManager);
            services.AddOpenIdConnectMetadataWarmup();
        });

        await host.StartAsync();
        await Task.WhenAll(firstManager.Called.Task, secondManager.Called.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, firstManager.CallCount);
        Assert.Equal(1, secondManager.CallCount);
        Assert.Equal(0, nonOidcManager.CallCount);
    }

    [Fact]
    public async Task Hanging_metadata_read_does_not_block_other_managers_or_host_shutdown()
    {
        var hangingManager = new TrackingManager { IgnoreCancellation = true };
        var completedManager = new TrackingManager();
        var host = CreateHost(services =>
        {
            services.AddLogging();
            services.AddAuthentication()
                .AddOpenIdConnect(FirstScheme, options => options.ClientId = "hanging")
                .AddOpenIdConnect(SecondScheme, options => options.ClientId = "completed");
            services.PostConfigure<OpenIdConnectOptions>(
                FirstScheme, options => options.ConfigurationManager = hangingManager);
            services.PostConfigure<OpenIdConnectOptions>(
                SecondScheme, options => options.ConfigurationManager = completedManager);
            services.AddOpenIdConnectMetadataWarmup();
        });

        try
        {
            await host.StartAsync();
            await Task.WhenAll(hangingManager.Called.Task, completedManager.Called.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            host.Dispose();
        }

        Assert.Equal(1, hangingManager.CallCount);
        Assert.Equal(1, completedManager.CallCount);
    }

    private static ServiceProvider CreateBackchannelProvider(
        params (string Scheme, string ClientId, string ClientSecret, CapturingHandler Handler)[] schemes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var authentication = services.AddAuthentication();
        foreach (var scheme in schemes)
        {
            authentication.AddOpenIdConnect(scheme.Scheme, options =>
            {
                options.Authority = Authority;
                options.ClientId = scheme.ClientId;
                options.ClientSecret = scheme.ClientSecret;
                options.RequireHttpsMetadata = true;
            });
            services.AddHttpClient(scheme.Scheme)
                .UseOpenIdConnectClientSecretBasic(scheme.Scheme)
                .ConfigurePrimaryHttpMessageHandler(() => scheme.Handler);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static IHost CreateHost(Action<IServiceCollection> configureServices) =>
        new HostBuilder().ConfigureServices(configureServices).Build();

    private static HttpRequestMessage CreateTokenRequest() =>
        new(HttpMethod.Post, $"{Authority}/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = OpenIdConnectGrantTypes.AuthorizationCode,
                ["code"] = "authorization-code",
                ["client_id"] = "form-client",
                ["client_secret"] = "form-secret"
            })
        };

    private static string DecodeBasic(AuthenticationHeaderValue authorization)
    {
        Assert.Equal("Basic", authorization.Scheme);
        return Encoding.UTF8.GetString(
            Convert.FromBase64String(Assert.IsType<string>(authorization.Parameter)));
    }

    private static string CreateDiscoveryJson(string issuer) => $$"""
        {
          "issuer":"{{issuer}}",
          "token_endpoint":"{{issuer}}/connect/token"
        }
        """;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string DiscoveryJson { get; init; } = "{}";
        public List<AuthenticationHeaderValue> AuthorizationHeaders { get; } = [];
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization,
                body));
            if (request.Headers.Authorization is not null)
                AuthorizationHeaders.Add(request.Headers.Authorization);

            var responseBody = request.Method == HttpMethod.Get
                ? DiscoveryJson
                : """{"access_token":"access"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Url,
        AuthenticationHeaderValue? Authorization,
        string Body);

    private sealed class TrackingManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly TaskCompletionSource<OpenIdConnectConfiguration> configuration =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public TaskCompletionSource Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation { get; init; }
        public int CallCount => Volatile.Read(ref callCount);

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            Called.TrySetResult();
            if (IgnoreCancellation)
                return configuration.Task;
            return Task.FromResult(new OpenIdConnectConfiguration
            {
                Issuer = Authority,
                TokenEndpoint = Authority + "/connect/token"
            });
        }

        public void RequestRefresh()
        {
        }
    }
}
