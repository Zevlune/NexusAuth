using Demo.Bff.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Demo.Bff.Services;

/// <summary>
/// 统一处理 BFF Cookie 会话的有效性检查与自动续签。
/// 主要调用方：ASP.NET Core Cookie 中间件，在每次请求验证 Principal 时自动触发。
/// </summary>
public sealed class OidcSessionCookieEvents : CookieAuthenticationEvents
{
    private readonly OidcBffService _oidcBffService;
    private readonly ILogger<OidcSessionCookieEvents> _logger;

    public OidcSessionCookieEvents(OidcBffService oidcBffService, ILogger<OidcSessionCookieEvents> logger)
    {
        _oidcBffService = oidcBffService;
        _logger = logger;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null)
        {
            context.RejectPrincipal();
            return;
        }

        SessionRenewalResult? result;
        try
        {
            result = await _oidcBffService.EnsureActiveSessionAsync(context.Principal, context.HttpContext.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            // NexusAuth 不可用导致续签失败时按未登录处理（401），而不是让整个请求 500。
            _logger.LogWarning(ex, "Session renewal failed because the identity provider is unreachable.");
            context.RejectPrincipal();
            return;
        }

        if (result is null)
        {
            context.RejectPrincipal();
            return;
        }

        if (result.IsRefreshed)
        {
            context.ReplacePrincipal(result.Principal);
            context.ShouldRenew = true;
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
