using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using NexusAuth.Workbench.Api;
using NexusAuth.Workbench.Api.Options;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchLoginRateLimitTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void Partition_key_normalizes_mapped_ipv4_and_groups_ipv6_by_prefix(string address, string expected) =>
        Assert.Equal(expected, WorkbenchLoginRateLimit.PartitionKey(IPAddress.Parse(address)));

    [Fact]
    public void Different_ipv6_prefixes_and_missing_addresses_get_their_own_partitions()
    {
        Assert.NotEqual(
            WorkbenchLoginRateLimit.PartitionKey(IPAddress.Parse("2001:db8:1:2::1")),
            WorkbenchLoginRateLimit.PartitionKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("unknown", WorkbenchLoginRateLimit.PartitionKey(null));
    }

    [Fact]
    public void Shipped_proxy_network_resolves_browser_addresses_from_the_dashboard_nginx()
    {
        // docker/workbench.appsettings.json trusts the compose workbench-proxy subnet.
        var options = new ForwardedHeadersOptions();
        WorkbenchSecurityOptions.ConfigureForwardedHeaders(options, new WorkbenchSecurityOptions
        {
            ForwardedHeadersEnabled = true,
            KnownNetworks = "172.30.56.0/28",
        });

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        var network = Assert.Single(options.KnownIPNetworks);
        Assert.True(network.Contains(IPAddress.Parse("172.30.56.3")));
        Assert.False(network.Contains(IPAddress.Parse("172.30.56.16")));
    }
}
