using Microsoft.AspNetCore.Mvc;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;

namespace NexusAuth.Host.Authentication;

internal static class TotpEnrollmentFlow
{
    public const string CookieName = "NexusAuth.PendingTotp";
    public const string Path = "/account/two-factor-setup";

    public static async Task<IActionResult> StartAsync(HttpContext context, User user, string? returnUrl,
        bool rememberMe, string methods, LoginFlowOptions options, ITotpService totp,
        ILoginFlowStateProtector protector, CancellationToken ct)
    {
        var enrollment = await totp.BeginEnrollmentAsync(user.Id, ct);
        var lifetime = TimeSpan.FromMinutes(options.PendingStateLifetimeMinutes);
        var state = new PendingLoginFlowState(user.Id, user.Username, returnUrl, rememberMe,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), methods, enrollment.ProtectedSecret,
            user.TokenInvalidBefore?.UtcTicks ?? 0);
        context.Response.Cookies.Append(CookieName, protector.Protect(state, lifetime), new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
            Path = Path, MaxAge = lifetime, IsEssential = true,
        });
        return new RedirectResult(Path);
    }

    public static void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName,
        new CookieOptions { Path = Path, HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict });
}
