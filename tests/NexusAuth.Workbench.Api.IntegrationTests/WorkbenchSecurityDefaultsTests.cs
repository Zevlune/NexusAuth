using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api;
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

        Assert.Contains("NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY", exception.ToString());
    }

    [Fact]
    public void Require_https_metadata_defaults_to_true_and_is_forwarded_to_oidc()
    {
        using var factory = CreateFactory();
        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(WorkbenchAuthenticationDefaults.BearerScheme);
        var oidc = factory.Services.GetRequiredService<IOptions<WorkbenchAuthOptions>>().Value;

        Assert.True(jwt.RequireHttpsMetadata);
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
        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(WorkbenchAuthenticationDefaults.BearerScheme);
        var oidc = factory.Services.GetRequiredService<IOptions<WorkbenchAuthOptions>>().Value;

        Assert.False(jwt.RequireHttpsMetadata);
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
                Configuration(new Dictionary<string, string?>
                {
                    ["Security:ForwardedHeadersEnabled"] = "true",
                })));

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
    public void Security_environment_variables_map_to_configuration()
    {
        var values = new Dictionary<string, string>
        {
            ["NEXUSAUTH_WORKBENCH_SECURITY_COOKIE_SECURE_POLICY"] = "SameAsRequest",
            ["NEXUSAUTH_WORKBENCH_SECURITY_FORWARDED_HEADERS_ENABLED"] = "true",
            ["NEXUSAUTH_WORKBENCH_SECURITY_FORWARD_LIMIT"] = "2",
            ["NEXUSAUTH_WORKBENCH_SECURITY_KNOWN_PROXIES"] = "10.0.0.2",
            ["NEXUSAUTH_WORKBENCH_SECURITY_KNOWN_NETWORKS"] = "10.0.0.0/8",
            ["NEXUSAUTH_WORKBENCH_SWAGGER_ENABLED"] = "false",
        };
        var originalValues = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
            using var configuration = new ConfigurationManager();

            configuration.AddWorkbenchEnvironmentVariables();

            Assert.Equal("SameAsRequest", configuration["Security:CookieSecurePolicy"]);
            Assert.Equal("true", configuration["Security:ForwardedHeadersEnabled"]);
            Assert.Equal("2", configuration["Security:ForwardLimit"]);
            Assert.Equal("10.0.0.2", configuration["Security:KnownProxies"]);
            Assert.Equal("10.0.0.0/8", configuration["Security:KnownNetworks"]);
            Assert.Equal("false", configuration["Swagger:Enabled"]);
        }
        finally
        {
            foreach (var (name, value) in originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
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
            Configuration(new Dictionary<string, string?>
            {
                ["Security:ForwardedHeadersEnabled"] = "true",
                ["Security:KnownProxies"] = trustedProxy,
            }));
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

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
