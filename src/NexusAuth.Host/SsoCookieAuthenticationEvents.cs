using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace NexusAuth.Host;

public class SsoCookieAuthenticationEvents(
    ISsoSessionService sessionService,
    IUserService userService,
    ISecurityPolicyService securityPolicyService) : CookieAuthenticationEvents, ISsoCookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var subject = context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var session = context.Principal?.FindFirst("sid")?.Value;
        if (!Guid.TryParse(subject, out var userId)
            || !Guid.TryParse(session, out var sessionId)
            || !await sessionService.IsActiveAsync(sessionId, userId, context.HttpContext.RequestAborted))
        {
            await RejectAsync(context);
            return;
        }

        // 中文注释：每次请求都重新评估用户安全策略，加入黑名单或移出白名单的用户立即失去现有 SSO 会话。
        var user = await userService.FindByIdAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || !user.IsActive || !securityPolicyService.CheckUser(user).IsSuccess)
            await RejectAsync(context);
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AppWebModule.AuthenticationScheme);
    }
}
