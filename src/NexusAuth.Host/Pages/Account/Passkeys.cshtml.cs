using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Application.Users;
using NexusAuth.Host.Authentication;

namespace NexusAuth.Host.Pages.Account;

[Authorize(AuthenticationSchemes = AppWebModule.AuthenticationScheme)]
public sealed class PasskeysModel(
    IPasskeyManagementService passkeys,
    IUserService users,
    ITotpService totp,
    IWebAuthnEnrollmentStateProtector enrollment,
    IOptions<WebAuthnOptions> options,
    IOptions<LoginFlowOptions> loginFlow) : PageModel
{
    [BindProperty] public string CurrentPassword { get; set; } = string.Empty;
    [BindProperty] public string TotpCode { get; set; } = string.Empty;
    [BindProperty] public Guid CredentialId { get; set; }
    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    public IReadOnlyList<WebAuthnCredentialSummary> Credentials { get; private set; } = [];
    public bool PasskeyEnabled => options.Value.Enabled;
    public bool TotpEnabled { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? StatusMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        if (!PasskeyEnabled) return NotFound();
        if (!await VerifyIdentityAsync(ct)) return await ShowAsync(ct);
        var token = enrollment.Protect(new WebAuthnEnrollmentState(UserId, "/account/passkeys"), TimeSpan.FromMinutes(5));
        return RedirectToPage("/Account/PasskeyEnrollment", new { token });
    }

    public async Task<IActionResult> OnPostRenameAsync(CancellationToken ct)
    {
        try
        {
            if (!await passkeys.RenameAsync(UserId, CredentialId, DisplayName, ct))
                ErrorMessage = "凭据不存在或已被撤销。";
            else StatusMessage = "Passkey 名称已更新。";
        }
        catch (ArgumentException exception) { ErrorMessage = exception.Message; }
        return await ShowAsync(ct);
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        // Every account currently has a password. Verify that fallback (and any
        // configured second factor) before removing even the last Passkey.
        if (!await VerifyIdentityAsync(ct)) return await ShowAsync(ct);
        var credentials = await passkeys.GetAsync(UserId, ct);
        var requiresTotp = string.Equals(loginFlow.Value.FindStep(LoginFlowStepTypes.Totp)?.Requirement,
            LoginFlowRequirements.Required, StringComparison.OrdinalIgnoreCase);
        if (requiresTotp && !await totp.IsEnabledAsync(UserId, ct)
            && credentials.Count(credential => !credential.DisabledAt.HasValue) == 1
            && credentials.Any(credential => credential.Id == CredentialId && !credential.DisabledAt.HasValue))
        {
            ErrorMessage = "当前登录配置要求身份验证器，请先绑定身份验证器，再删除最后一个 Passkey。";
            return await ShowAsync(ct);
        }
        if (!await passkeys.RevokeAsync(UserId, CredentialId, UserId, "用户在个人中心删除", ct))
        {
            ErrorMessage = "凭据不存在或已被撤销。";
            return await ShowAsync(ct);
        }
        await HttpContext.SignOutAsync(AppWebModule.AuthenticationScheme);
        return RedirectToPage("/Account/Login", new { ReturnUrl = "/account/passkeys" });
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> VerifyIdentityAsync(CancellationToken ct)
    {
        var current = await users.FindByIdAsync(UserId, ct);
        var verified = current is null || string.IsNullOrWhiteSpace(CurrentPassword)
            ? null : await users.ValidateCredentialsAsync(current.Username, CurrentPassword, ct);
        CurrentPassword = string.Empty;
        ModelState.Remove(nameof(CurrentPassword));
        if (verified is null || verified.Id != UserId)
        {
            ErrorMessage = "身份验证失败，请检查密码或稍后重试。";
            return false;
        }
        var valid = !await totp.IsEnabledAsync(UserId, ct) || await totp.ValidateAsync(UserId, TotpCode, ct);
        TotpCode = string.Empty;
        ModelState.Remove(nameof(TotpCode));
        if (!valid) ErrorMessage = "动态验证码无效或已经使用。";
        return valid;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Credentials = await passkeys.GetAsync(UserId, ct);
        TotpEnabled = await totp.IsEnabledAsync(UserId, ct);
    }

    private async Task<IActionResult> ShowAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }
}
