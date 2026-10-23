using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NexusAuth.Application.Users;

namespace NexusAuth.Host.Pages.Account;

[Authorize(AuthenticationSchemes = AppWebModule.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ChangePasswordModel(IUserService userService, ITotpService totpService) : PageModel
{
    private readonly IUserService userService = userService;

    [BindProperty]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmNewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string TotpCode { get; set; } = string.Empty;

    public bool TotpEnabled { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Challenge(AppWebModule.AuthenticationScheme);

        TotpEnabled = await totpService.IsEnabledAsync(userId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Challenge(AppWebModule.AuthenticationScheme);

        TotpEnabled = await totpService.IsEnabledAsync(userId, ct);
        try
        {
            if (NewPassword != ConfirmNewPassword)
            {
                ErrorMessage = "New passwords do not match.";
                return ClearSecretsAndShow();
            }

            if (TotpEnabled && !await VerifySecondFactorAsync(userId, ct))
                return ClearSecretsAndShow();

            await userService.ChangePasswordAsync(userId, CurrentPassword, NewPassword, ct);
            StatusMessage = "Password updated. Sign in again in applications that use refresh tokens.";
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }

        return ClearSecretsAndShow();
    }

    /// <summary>
    /// 中文注释：已绑定身份验证器时，与邮箱、Passkey 管理一致，先按用户 ID 校验当前密码，再校验动态码。
    /// 两者的失败都会计入登录锁定计数；先校验密码可避免因密码输错而白白消耗一次动态码。
    /// </summary>
    private async Task<bool> VerifySecondFactorAsync(Guid userId, CancellationToken ct)
    {
        var current = await userService.FindByIdAsync(userId, ct);
        var verified = current is null || string.IsNullOrWhiteSpace(CurrentPassword)
            ? null
            : await userService.ValidateCredentialsAsync(current.Username, CurrentPassword, ct);
        if (verified is null || verified.Id != userId)
        {
            ErrorMessage = "Current password is incorrect, or the account is temporarily locked.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TotpCode) || !await totpService.ValidateAsync(userId, TotpCode, ct))
        {
            ErrorMessage = "动态验证码无效或已经使用。";
            return false;
        }

        return true;
    }

    private PageResult ClearSecretsAndShow()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        TotpCode = string.Empty;
        ModelState.Remove(nameof(CurrentPassword));
        ModelState.Remove(nameof(NewPassword));
        ModelState.Remove(nameof(ConfirmNewPassword));
        ModelState.Remove(nameof(TotpCode));
        return Page();
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
}
