using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace NexusAuth.Host.Authentication;

/// <summary>Only configured proxy peers may supply forwarded client addresses.</summary>
public static class ClientIpConfiguration
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection("ClientIp");
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.ForwardedHeaders = ForwardedHeaders.None;
        if (!section.GetValue("ForwardedHeadersEnabled", false)) return;

        var limit = section.GetValue("ForwardLimit", 1);
        if (limit is < 1 or > 10)
            throw new InvalidOperationException("ClientIp:ForwardLimit must be between 1 and 10.");
        options.ForwardLimit = limit;
        foreach (var entry in Split(section["KnownProxies"]))
        {
            if (!IPAddress.TryParse(entry, out var address))
                throw new InvalidOperationException("ClientIp:KnownProxies contains an invalid IP address.");
            options.KnownProxies.Add(address);
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                options.KnownProxies.Add(address.MapToIPv6());
        }
        foreach (var entry in Split(section["KnownNetworks"]))
        {
            if (!System.Net.IPNetwork.TryParse(entry, out var network))
                throw new InvalidOperationException("ClientIp:KnownNetworks contains an invalid CIDR network.");
            options.KnownIPNetworks.Add(network);
        }
        // An empty allowlist must never imply trusting arbitrary request headers.
        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            throw new InvalidOperationException("Enable forwarded headers only with explicit ClientIp trusted proxies or networks.");
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }

    public static string? GetAddress(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return (address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address)?.ToString();
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
