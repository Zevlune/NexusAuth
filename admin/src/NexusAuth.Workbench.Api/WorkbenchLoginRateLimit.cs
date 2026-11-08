using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace NexusAuth.Workbench.Api;

/// <summary>
/// Per-client limit for starting anonymous Workbench logins. The partition is
/// the connection address after trusted forwarded headers were applied, so
/// the shipped compose files route the Dashboard nginx through a dedicated,
/// trusted proxy network; otherwise every browser shares nginx's address.
/// </summary>
public static class WorkbenchLoginRateLimit
{
    public const string PolicyName = "workbench-login";
    public const int PermitLimit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> GetPartition(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = PermitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = Window,
            });

    /// <summary>
    /// IPv4-mapped addresses collapse to IPv4, and IPv6 clients are grouped by
    /// their /64: a single subscriber usually controls the whole prefix, so
    /// rotating interface identifiers must not yield fresh buckets.
    /// </summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}
