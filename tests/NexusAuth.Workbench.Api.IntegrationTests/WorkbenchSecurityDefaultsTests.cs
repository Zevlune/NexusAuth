using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Workbench.Api;
using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Options;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

[Collection("Workbench environment")]
public sealed class WorkbenchSecurityDefaultsTests
{
    private const string Authority = "https://issuer.example.test";

    [Fact]
    public void Missing_authority_fails_startup()
    {
        using var factory = CreateFactory(authority: "");

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("Auth:Authority", exception.ToString());
    }

    [Fact]
    public void Require_https_metadata_defaults_to_true_and_is_forwarded_to_oidc()
    {
        using var factory = CreateFactory();
        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);

        Assert.True(oidc.RequireHttpsMetadata);
    }

    [Fact]
    public void Http_authority_is_rejected_when_require_https_metadata_defaults_to_true()
    {
        using var factory = CreateFactory(authority: "http://localhost:5100");

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("must use HTTPS", exception.ToString());
    }

    [Fact]
    public void Http_authority_is_allowed_only_when_explicitly_configured()
    {
        using var factory = CreateFactory(
            authority: "http://localhost:5100",
            settings: new() { ["Auth:RequireHttpsMetadata"] = "false" });
        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);

        Assert.False(oidc.RequireHttpsMetadata);
    }

    [Fact]
    public void Cookie_secure_policy_defaults_to_always_and_can_be_relaxed_for_local_http()
    {
        using var secure = CreateFactory();
        using var local = CreateFactory(
            settings: new() { ["Security:CookieSecurePolicy"] = "SameAsRequest" });

        Assert.Equal(CookieSecurePolicy.Always, GetCookieSecurePolicy(secure));
        Assert.Equal(CookieSecurePolicy.SameAsRequest, GetCookieSecurePolicy(local));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Unsafe")]
    public void Downgrading_cookie_secure_policy_fails_startup(string policy)
    {
        using var factory = CreateFactory(
            settings: new() { ["Security:CookieSecurePolicy"] = policy });

        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains("Security:CookieSecurePolicy", exception.ToString());
    }

    [Fact]
    public async Task Configured_proxy_is_trusted_and_untrusted_proxy_is_ignored()
    {
        var options = ConfigureProxy("10.0.0.2");
        var trusted = CreateContext("10.0.0.2");
        trusted.Request.Headers["X-Forwarded-For"] = "198.51.100.24";
        var untrusted = CreateContext("10.0.0.3");
        untrusted.Request.Headers["X-Forwarded-For"] = "198.51.100.24";

        await RunForwardedHeadersAsync(trusted, options);
        await RunForwardedHeadersAsync(untrusted, options);

        Assert.Equal("198.51.100.24", trusted.Connection.RemoteIpAddress?.ToString());
        Assert.Equal("10.0.0.3", untrusted.Connection.RemoteIpAddress?.ToString());
    }

    [Fact]
    public void Enabling_forwarded_headers_without_an_explicit_allowlist_fails()
    {
        var options = new ForwardedHeadersOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkbenchSecurityOptions.ConfigureForwardedHeaders(
                options,
                new WorkbenchSecurityOptions
                {
                    ForwardedHeadersEnabled = true,
                }));

        Assert.Contains("trusted proxies or networks", exception.Message);
    }

    [Fact]
    public async Task Api_and_oidc_callback_responses_disable_caching()
    {
        using var configured = new WorkbenchAdministrationAuthorizationFixture();
        using var client = configured.CreateAnonymousClient();

        foreach (var path in new[] { "/api/users", "/signin-oidc" })
        {
            using var response = await client.GetAsync(path);

            Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
            Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Pragma")));
        }
    }

    [Fact]
    public void Security_and_swagger_json_configuration_bind_to_options()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "Security": {
                "CookieSecurePolicy": "SameAsRequest",
                "ForwardedHeadersEnabled": true,
                "ForwardLimit": 2,
                "KnownProxies": "10.0.0.2",
                "KnownNetworks": "10.0.0.0/8"
              },
              "Swagger": {
                "Enabled": false
              }
            }
            """));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var security = configuration.GetSection(WorkbenchSecurityOptions.SectionName)
            .Get<WorkbenchSecurityOptions>();
        var swagger = configuration.GetSection(WorkbenchSwaggerOptions.SectionName)
            .Get<WorkbenchSwaggerOptions>();

        Assert.NotNull(security);
        Assert.Equal("SameAsRequest", security.CookieSecurePolicy);
        Assert.True(security.ForwardedHeadersEnabled);
        Assert.Equal(2, security.ForwardLimit);
        Assert.Equal("10.0.0.2", security.KnownProxies);
        Assert.Equal("10.0.0.0/8", security.KnownNetworks);
        Assert.NotNull(swagger);
        Assert.False(swagger.Enabled);
    }

    private static WebApplicationFactory<WorkbenchApiModule> CreateFactory(
        string authority = Authority,
        Dictionary<string, string>? settings = null)
    {
        return new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing")
                .UseSetting("Auth:Authority", authority)
                .UseSetting("Auth:Audience", "nexusauth.workbench.api")
                .UseSetting("Auth:ClientId", "nexusauth.workbench")
                .UseSetting("Auth:ClientSecret", "test-only-secret")
                .UseSetting("Auth:RedirectUri", "https://app.test/signin-oidc")
                .UseSetting("Auth:PostLogoutRedirectUri", "https://app.test/")
                .UseSetting("Auth:Scope", "openid profile offline_access nexusauth.workbench.api");
            foreach (var (key, value) in settings ?? [])
                builder.UseSetting(key, value);
        });
    }

    private static CookieSecurePolicy GetCookieSecurePolicy(
        WebApplicationFactory<WorkbenchApiModule> factory) =>
        factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(WorkbenchAuthenticationDefaults.CookieScheme)
            .Cookie.SecurePolicy;

    private static ForwardedHeadersOptions ConfigureProxy(string trustedProxy)
    {
        var options = new ForwardedHeadersOptions();
        WorkbenchSecurityOptions.ConfigureForwardedHeaders(
            options,
            new WorkbenchSecurityOptions
            {
                ForwardedHeadersEnabled = true,
                KnownProxies = trustedProxy,
            });
        return options;
    }

    private static DefaultHttpContext CreateContext(string socketPeer) => new()
    {
        Connection = { RemoteIpAddress = IPAddress.Parse(socketPeer) },
    };

    private static async Task RunForwardedHeadersAsync(
        DefaultHttpContext context,
        ForwardedHeadersOptions options)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<ForwardedHeadersOptions>(configured =>
            {
                configured.ForwardedHeaders = options.ForwardedHeaders;
                configured.ForwardLimit = options.ForwardLimit;
                configured.KnownProxies.Clear();
                foreach (var proxy in options.KnownProxies)
                    configured.KnownProxies.Add(proxy);
                configured.KnownIPNetworks.Clear();
                foreach (var network in options.KnownIPNetworks)
                    configured.KnownIPNetworks.Add(network);
            })
            .BuildServiceProvider();

        var app = new ApplicationBuilder(services);
        app.UseForwardedHeaders();
        app.Run(_ => Task.CompletedTask);
        await app.Build()(context);
    }
}
