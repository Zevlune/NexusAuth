using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Host.Authentication;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class ClientIpTests
{
    [Fact]
    public async Task Forwarded_headers_leave_the_socket_peer_when_no_forwarded_values_are_present()
    {
        var context = CreateContext("10.0.0.2");

        await RunMiddlewareAsync(context, Configure("10.0.0.0/8"));

        Assert.Equal("10.0.0.2", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public async Task Trusted_proxy_can_supply_forwarded_client_address()
    {
        var context = CreateContext("10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.24";

        await RunMiddlewareAsync(context, Configure("10.0.0.0/8"));

        Assert.Equal("198.51.100.24", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public async Task Untrusted_peer_cannot_override_socket_address_with_forwarded_header()
    {
        var context = CreateContext("203.0.113.8");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.24";

        await RunMiddlewareAsync(context, Configure("10.0.0.0/8"));

        Assert.Equal("203.0.113.8", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public void GetAddress_normalizes_mapped_ipv4_and_preserves_native_ipv6()
    {
        var mapped = CreateContext("::ffff:192.0.2.33");
        var ipv6 = CreateContext("2001:db8::45");

        Assert.Equal("192.0.2.33", ClientIpConfiguration.GetAddress(mapped));
        Assert.Equal("2001:db8::45", ClientIpConfiguration.GetAddress(ipv6));
    }

    [Fact]
    public async Task Forward_limit_controls_how_many_trusted_hops_are_consumed()
    {
        var limitOne = CreateContext("10.0.0.2");
        limitOne.Request.Headers["X-Forwarded-For"] = "198.51.100.24, 10.0.0.1";
        var limitTwo = CreateContext("10.0.0.2");
        limitTwo.Request.Headers["X-Forwarded-For"] = "198.51.100.24, 10.0.0.1";

        await RunMiddlewareAsync(limitOne, Configure("10.0.0.0/8", forwardLimit: 1));
        await RunMiddlewareAsync(limitTwo, Configure("10.0.0.0/8", forwardLimit: 2));

        Assert.Equal("10.0.0.1", ClientIpConfiguration.GetAddress(limitOne));
        Assert.Equal("198.51.100.24", ClientIpConfiguration.GetAddress(limitTwo));
    }

    [Fact]
    public async Task Forwarded_chain_stops_at_first_untrusted_hop()
    {
        var context = CreateContext("10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.24, 203.0.113.9";

        await RunMiddlewareAsync(context, Configure("10.0.0.0/8", forwardLimit: 5));

        Assert.Equal("203.0.113.9", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public async Task Malformed_forwarded_address_is_not_used_as_client_ip()
    {
        var context = CreateContext("10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "not-an-ip";

        await RunMiddlewareAsync(context, Configure("10.0.0.0/8"));

        Assert.Equal("10.0.0.2", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public async Task Known_proxy_entry_trusts_only_the_configured_proxy_peer()
    {
        var trusted = CreateContext("10.0.0.2");
        trusted.Request.Headers["X-Forwarded-For"] = "198.51.100.24";
        var untrusted = CreateContext("10.0.0.3");
        untrusted.Request.Headers["X-Forwarded-For"] = "198.51.100.24";

        await RunMiddlewareAsync(trusted, ConfigureProxy("10.0.0.2"));
        await RunMiddlewareAsync(untrusted, ConfigureProxy("10.0.0.2"));

        Assert.Equal("198.51.100.24", ClientIpConfiguration.GetAddress(trusted));
        Assert.Equal("10.0.0.3", ClientIpConfiguration.GetAddress(untrusted));
    }

    [Fact]
    public async Task Configured_ipv4_proxy_trusts_ipv4_mapped_socket_peer()
    {
        var context = CreateContext("::ffff:10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.24";

        await RunMiddlewareAsync(context, ConfigureProxy("10.0.0.2"));

        Assert.Equal("198.51.100.24", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_when_disabled()
    {
        var context = CreateContext("10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.24";
        var options = new ForwardedHeadersOptions();
        ClientIpConfiguration.Configure(options, Configuration(new Dictionary<string, string?>
        {
            ["ClientIp:ForwardedHeadersEnabled"] = "false",
            ["ClientIp:KnownNetworks"] = "10.0.0.0/8"
        }));

        await RunMiddlewareAsync(context, options);

        Assert.Equal("10.0.0.2", ClientIpConfiguration.GetAddress(context));
    }

    [Fact]
    public void Enabling_forwarded_headers_without_trusted_proxy_or_network_throws()
    {
        var options = new ForwardedHeadersOptions();

        var error = Assert.Throws<InvalidOperationException>(() =>
            ClientIpConfiguration.Configure(options, Configuration(new Dictionary<string, string?>
            {
                ["ClientIp:ForwardedHeadersEnabled"] = "true"
            })));

        Assert.Contains("trusted proxies or networks", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultHttpContext CreateContext(string socketPeer)
    {
        return new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(socketPeer) }
        };
    }

    private static ForwardedHeadersOptions Configure(string trustedNetwork, int forwardLimit = 1)
    {
        var options = new ForwardedHeadersOptions();
        ClientIpConfiguration.Configure(options, Configuration(new Dictionary<string, string?>
        {
            ["ClientIp:ForwardedHeadersEnabled"] = "true",
            ["ClientIp:KnownNetworks"] = trustedNetwork,
            ["ClientIp:ForwardLimit"] = forwardLimit.ToString()
        }));
        return options;
    }

    private static ForwardedHeadersOptions ConfigureProxy(string trustedProxy, int forwardLimit = 1)
    {
        var options = new ForwardedHeadersOptions();
        ClientIpConfiguration.Configure(options, Configuration(new Dictionary<string, string?>
        {
            ["ClientIp:ForwardedHeadersEnabled"] = "true",
            ["ClientIp:KnownProxies"] = trustedProxy,
            ["ClientIp:ForwardLimit"] = forwardLimit.ToString()
        }));
        return options;
    }

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static async Task RunMiddlewareAsync(DefaultHttpContext context, ForwardedHeadersOptions options)
    {
        var services = new ServiceCollection().AddLogging().Configure<ForwardedHeadersOptions>(configured =>
        {
            configured.ForwardedHeaders = options.ForwardedHeaders;
            configured.ForwardLimit = options.ForwardLimit;
            configured.KnownProxies.Clear();
            foreach (var proxy in options.KnownProxies) configured.KnownProxies.Add(proxy);
            configured.KnownIPNetworks.Clear();
            foreach (var network in options.KnownIPNetworks) configured.KnownIPNetworks.Add(network);
        }).BuildServiceProvider();

        var app = new ApplicationBuilder(services);
        app.UseForwardedHeaders();
        app.Run(_ => Task.CompletedTask);
        await app.Build()(context);
    }
}
