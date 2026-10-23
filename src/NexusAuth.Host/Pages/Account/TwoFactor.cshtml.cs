using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Authentication;

namespace NexusAuth.Host.Pages.Account;

[Authorize(AuthenticationSchemes = AppWebModule.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TwoFactorModel(
    ITotpService totpService,
    IUserService userService,
    ISsoSessionService sessionService,
    IOptions<LoginFlowOptions> flowOptions) : PageModel
{
    private readonly LoginFlowOptions _flowOptions = flowOptions.Value;

    [BindProperty]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 现有身份验证器生成的动态码；已启用 TOTP 时，添加或移除身份验证器都必须提供。
    /// </summary>
    [BindProperty]
    public string CurrentTotpCode { get; set; } = string.Empty;

    /// <summary>
    /// 待确认凭据的 Data Protection 密文，仅用于在服务端定位待确认凭据。
    /// 明文密钥与 otpauth 链接始终由服务端重新生成，不接受页面回传。
    /// </summary>
    [BindProperty]
    public string ProtectedEnrollmentSecret { get; set; } = string.Empty;

    [BindProperty]
    public Guid CredentialId { get; set; }

    public string EnrollmentSecret { get; private set; } = string.Empty;

    public string OtpAuthUri { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public bool IsEnrollmentPending { get; private set; }

    public IReadOnlyList<TotpCredentialSummary> Credentials { get; private set; } = [];

    public bool CanDisable => !string.Equals(
        _flowOptions.FindStep(LoginFlowStepTypes.Totp)?.Requirement,
        LoginFlowRequirements.Required,
        StringComparison.OrdinalIgnoreCase);

    public string? ErrorMessage { get; private set; }

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!TryGetUserId(out _))
            return Challenge(AppWebModule.AuthenticationScheme);

        await LoadCredentialsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostBeginAsync(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Challenge(AppWebModule.AuthenticationScheme);

        var user = await VerifyIdentityAsync(userId, ct);
        if (user is null)
        {
            await LoadCredentialsAsync(ct);
            return Page();
        }

        var enrollment = await totpService.BeginEnrollmentAsync(user.Id, ct);
        ShowEnrollment(enrollment);
        await LoadCredentialsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Challenge(AppWebModule.AuthenticationScheme);

        await LoadCredentialsAsync(ct);

        // 中文注释：根据服务端保存的待确认凭据重建密钥和配置链接，避免渲染页面回传的 otpauth（例如 javascript: 链接）。
        var pending = await totpService.FindPendingEnrollmentAsync(userId, ProtectedEnrollmentSecret, ct);
        if (pending is null)
        {
            ProtectedEnrollmentSecret = string.Empty;
            Code = string.Empty;
            ErrorMessage = "The enrollment has expired. Start over.";
            return Page();
        }

        if (!await totpService.ConfirmEnrollmentAsync(userId, pending.ProtectedSecret, Code, ct))
        {
            ShowEnrollment(pending);
            Code = string.Empty;
            ModelState.Remove(nameof(Code));
            ErrorMessage = "The code is invalid or the enrollment has expired. Start over if necessary.";
            return Page();
        }

        ProtectedEnrollmentSecret = string.Empty;
        Code = string.Empty;
        IsEnrollmentPending = false;
        await LoadCredentialsAsync(ct);
        StatusMessage = "Authenticator verification is enabled.";
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
            return Challenge(AppWebModule.AuthenticationScheme);

        if (!CanDisable)
        {
            ErrorMessage = "The active login flow requires TOTP and does not allow it to be disabled.";
            await LoadCredentialsAsync(ct);
            return Page();
        }

        var user = await VerifyIdentityAsync(userId, ct);
        if (user is null)
        {
            await LoadCredentialsAsync(ct);
            return Page();
        }

        if (!await totpService.DisableAsync(user.Id, CredentialId, ct))
        {
            ErrorMessage = "The authenticator is no longer available.";
            await LoadCredentialsAsync(ct);
            return Page();
        }
        await sessionService.RevokeAllForUserAsync(user.Id, ct);
        await HttpContext.SignOutAsync(AppWebModule.AuthenticationScheme);
        return RedirectToPage("/Account/Login");
    }

    /// <summary>
    /// 中文注释：按会话中的用户 ID 重新校验当前密码（不使用可能已过期的用户名 Claim），
    /// 已启用身份验证器时还必须提供现有身份验证器的动态码。密码和动态码失败都会计入登录锁定。
    /// </summary>
    private async Task<User?> VerifyIdentityAsync(Guid userId, CancellationToken ct)
    {
        var password = CurrentPassword;
        var totpCode = CurrentTotpCode;
        CurrentPassword = string.Empty;
        CurrentTotpCode = string.Empty;
        ModelState.Remove(nameof(CurrentPassword));
        ModelState.Remove(nameof(CurrentTotpCode));

        var current = await userService.FindByIdAsync(userId, ct);
        var verified = current is null || string.IsNullOrWhiteSpace(password)
            ? null
            : await userService.ValidateCredentialsAsync(current.Username, password, ct);
        if (verified is null || verified.Id != userId)
        {
            ErrorMessage = "Current password is incorrect, or the account is temporarily locked.";
            return null;
        }

        if (await totpService.IsEnabledAsync(userId, ct)
            && (string.IsNullOrWhiteSpace(totpCode) || !await totpService.ValidateAsync(userId, totpCode, ct)))
        {
            ErrorMessage = "动态验证码无效或已经使用。";
            return null;
        }

        return verified;
    }

    private void ShowEnrollment(TotpEnrollment enrollment)
    {
        EnrollmentSecret = enrollment.ManualEntryKey;
        ProtectedEnrollmentSecret = enrollment.ProtectedSecret;
        OtpAuthUri = enrollment.OtpauthUri;
        IsEnrollmentPending = true;
    }

    private async Task LoadCredentialsAsync(CancellationToken ct)
    {
        if (TryGetUserId(out var userId))
        {
            Credentials = await totpService.GetCredentialsAsync(userId, ct);
            IsEnabled = Credentials.Any(credential => credential.IsEnabled);
        }
    }

    private bool TryGetUserId(out Guid userId)
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}
