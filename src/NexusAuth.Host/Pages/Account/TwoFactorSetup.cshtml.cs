using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Users;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Authentication;

namespace NexusAuth.Host.Pages.Account;

[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TwoFactorSetupModel(IUserService users, ITotpService totp,
    ILoginFlowStateProtector protector, IWebSignInService signIn,
    ISecurityPolicyService policy, ISecondFactorRecoveryService recovery) : PageModel
{
    [BindProperty] public string Code { get; set; } = string.Empty;
    public TotpEnrollment? Enrollment { get; private set; }
    public IReadOnlyList<string> RecoveryCodes { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public string ContinueUrl { get; private set; } = "/account";
    public bool SignedIn { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var pending = await LoadAsync(ct);
        return pending is null ? Restart() : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var pending = await LoadAsync(ct);
        if (pending is null) return Restart();
        var (user, flow) = pending.Value;
        if (!await totp.ConfirmEnrollmentAsync(user.Id, Enrollment!.ProtectedSecret, Code, flow.TokenEpoch, ct))
        {
            Code = string.Empty;
            ModelState.Remove(nameof(Code));
            ErrorMessage = "验证码无效、已使用或账号暂时锁定，请稍后重试。";
            return Page();
        }
        // Confirmation reloads the user; keep the original primary-factor
        // proof bound to the same security epoch after that reload as well.
        if (flow.TokenEpoch != (user.TokenInvalidBefore?.UtcTicks ?? 0))
            return Restart(flow.ReturnUrl);
        try
        {
            await signIn.SignInAsync(HttpContext, user, flow.RememberMe,
                DateTimeOffset.FromUnixTimeSeconds(flow.AuthenticatedAtUnixSeconds), flow.AuthenticationMethods + " otp", ct);
        }
        catch (InvalidOperationException)
        {
            return Restart(flow.ReturnUrl);
        }

        // The session now exists. A recovery-code failure (for example the
        // account lock being briefly held) must not bounce a signed-in user
        // back to the login page; they can generate codes later.
        TotpEnrollmentFlow.Clear(HttpContext);
        Enrollment = null;
        SignedIn = true;
        try
        {
            RecoveryCodes = await recovery.GenerateCodesAsync(user.Id, ct);
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = "身份验证器已启用，但恢复码暂未生成。请稍后在“账户安全 → 身份验证器”中重新生成恢复码。";
        }
        return Page();
    }

    private async Task<(User User, PendingLoginFlowState Flow)?> LoadAsync(CancellationToken ct)
    {
        if (!protector.TryUnprotect(Request.Cookies[TotpEnrollmentFlow.CookieName], out var flow)
            || flow?.EnrollmentProtectedSecret is null || flow.TokenEpoch is null)
            return null;
        var user = await users.FindByIdAsync(flow.UserId, ct);
        if (user is not { IsActive: true } || user.IsLoginLocked(DateTimeOffset.UtcNow)
            || !policy.CheckUser(user).IsSuccess
            || (user.TokenInvalidBefore?.UtcTicks ?? 0) != flow.TokenEpoch)
            return null;
        Enrollment = await totp.FindPendingEnrollmentAsync(user.Id, flow.EnrollmentProtectedSecret, ct);
        if (Enrollment is null || await totp.IsEnabledAsync(user.Id, ct)) return null;
        ContinueUrl = !string.IsNullOrWhiteSpace(flow.ReturnUrl) && Url.IsLocalUrl(flow.ReturnUrl)
            ? flow.ReturnUrl : "/account";
        return (user, flow);
    }

    private IActionResult Restart(string? returnUrl = null)
    {
        TotpEnrollmentFlow.Clear(HttpContext);
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}")
            : Redirect("/account/login");
    }
}
