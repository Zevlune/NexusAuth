using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api;

internal static class WorkbenchLoginFlow
{
    public const string PropertyKey = "NexusAuth.LoginFlow";
    public const string ValidatedKey = "NexusAuth.LoginFlow.Validated";
    public const string ExchangeStartedKey = "NexusAuth.LoginFlow.ExchangeStarted";

    public static string Hash(string binding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));

    public static string CookieName(string flow, bool secure) =>
        $"{(secure ? "__Host-" : ".")}NexusAuth.Workbench.Oidc.{flow}";

    public static CookieOptions CookieOptions(HttpContext context,
        IOptionsMonitor<CookieAuthenticationOptions> options, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps || options.Get(WorkbenchAuthenticationDefaults.CookieScheme)
            .Cookie.SecurePolicy == CookieSecurePolicy.Always,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime,
        IsEssential = true,
    };
}
