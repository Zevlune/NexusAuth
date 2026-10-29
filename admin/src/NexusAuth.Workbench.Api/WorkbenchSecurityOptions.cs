using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using CookieSecurePolicyValue = Microsoft.AspNetCore.Http.CookieSecurePolicy;

namespace NexusAuth.Workbench.Api;

public sealed class WorkbenchSecurityOptions
{
    public const string SectionName = "Security";

    public string CookieSecurePolicy { get; set; } = nameof(CookieSecurePolicyValue.Always);

    public bool ForwardedHeadersEnabled { get; set; }

    public int ForwardLimit { get; set; } = 1;

    public string? KnownProxies { get; set; }

    public string? KnownNetworks { get; set; }

    public static CookieSecurePolicyValue ResolveCookieSecurePolicy(WorkbenchSecurityOptions options) =>
        TryParseCookieSecurePolicy(options.CookieSecurePolicy, out var policy)
            ? policy
            : throw new InvalidOperationException(
                "Security:CookieSecurePolicy must be SameAsRequest or Always.");

    public static void ConfigureForwardedHeaders(
        ForwardedHeadersOptions options,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.ForwardedHeaders = ForwardedHeaders.None;
        if (!section.GetValue("ForwardedHeadersEnabled", false))
            return;

        var forwardLimit = section.GetValue("ForwardLimit", 1);
        if (forwardLimit is < 1 or > 10)
            throw new InvalidOperationException("Security:ForwardLimit must be between 1 and 10.");
        options.ForwardLimit = forwardLimit;

        foreach (var entry in Split(section["KnownProxies"]))
        {
            if (!IPAddress.TryParse(entry, out var address))
                throw new InvalidOperationException("Security:KnownProxies contains an invalid IP address.");
            options.KnownProxies.Add(address);
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                options.KnownProxies.Add(address.MapToIPv6());
        }

        foreach (var entry in Split(section["KnownNetworks"]))
        {
            if (!System.Net.IPNetwork.TryParse(entry, out var network))
                throw new InvalidOperationException("Security:KnownNetworks contains an invalid CIDR network.");
            options.KnownIPNetworks.Add(network);
        }

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "Enable forwarded headers only with explicit Security trusted proxies or networks.");
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }

    internal static bool TryParseCookieSecurePolicy(string? value, out CookieSecurePolicyValue policy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            policy = CookieSecurePolicyValue.Always;
            return true;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out policy)
            && policy is CookieSecurePolicyValue.Always or CookieSecurePolicyValue.SameAsRequest
            && !int.TryParse(value, out _))
        {
            return true;
        }

        policy = CookieSecurePolicyValue.Always;
        return false;
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split(
            [',', ';'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
