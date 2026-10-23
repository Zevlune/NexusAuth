using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NexusAuth.Application.Services.LoginAudits;

namespace NexusAuth.Host.Pages.Account;

[Authorize(AuthenticationSchemes = AppWebModule.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginHistoryModel(ILoginAuditService loginAuditService) : PageModel
{
    // 中文注释：审计表按用户 ID 记录成功与失败（动态码错误、策略拒绝、Passkey 校验失败等），
    // 页面如实展示结果和原因，方便用户发现针对自己账号的异常尝试。
    private static readonly IReadOnlyDictionary<string, string> FailureReasonLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["InvalidCredentials"] = "账号或密码错误",
            ["MissingCredentials"] = "未填写账号或密码",
            ["SliderCaptchaFailed"] = "拼图验证未通过",
            ["UserDeniedByPolicy"] = "被安全策略拒绝",
            ["TotpEnrollmentRequired"] = "未绑定身份验证器",
            ["InvalidTotp"] = "动态验证码错误",
            ["UserLocked"] = "失败次数过多，账号暂时锁定",
            ["UserUnavailable"] = "账号不可用",
            ["InvalidPasskey"] = "Passkey 校验失败",
            ["PasskeyCounterRegression"] = "Passkey 计数器异常",
        };

    public IReadOnlyList<LoginAuditLogDto> LoginAudits { get; private set; } = [];

    public static string DescribeResult(LoginAuditLogDto audit) => audit.IsSuccessful ? "成功" : "失败";

    public static string? DescribeFailureReason(LoginAuditLogDto audit)
    {
        if (audit.IsSuccessful)
            return null;

        return audit.FailureReason is { } reason && FailureReasonLabels.TryGetValue(reason, out var label)
            ? label
            : "其他原因";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var userIdValue = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdValue, out var userId))
            return Challenge(AppWebModule.AuthenticationScheme);

        LoginAudits = await loginAuditService.GetRecentForUserAsync(userId, 20, ct);
        return Page();
    }
}
