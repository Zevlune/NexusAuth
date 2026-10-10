using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NexusAuth.Application.Users;
using NexusAuth.Application.Services.Email;

namespace NexusAuth.Host.Pages.Account;

[Authorize(AuthenticationSchemes = AppWebModule.AuthenticationScheme)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EmailModel(IUserService users, EmailAccountService emails, ITotpService totp) : PageModel
{
    [BindProperty] public string EmailAddress { get; set; } = "";
    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string TotpCode { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }
    public string? BoundEmail { get; private set; }
    public string MaskedEmail => MaskEmail(BoundEmail);
    public bool Bound { get; private set; }
    public bool TotpEnabled { get; private set; }
    public int SendCooldownSeconds { get; private set; }
    public int ConfiguredSendCooldownSeconds => emails.CooldownSeconds;
    public int CodeLifetimeMinutes => emails.LifetimeMinutes;
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public string? PasswordError { get; private set; }
    private Guid Id => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public async Task OnGetAsync(CancellationToken ct)
    {
        var user = await users.FindByIdAsync(Id, ct);
        EmailAddress = user?.Email ?? ""; Bound = !string.IsNullOrWhiteSpace(user?.Email);
        BoundEmail = user?.Email;
        if (Bound && Mode == "change") EmailAddress = "";
        TotpEnabled = await totp.IsEnabledAsync(Id, ct);
    }
    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        try {
            await emails.SendVerificationAsync(Id, EmailAddress, CurrentPassword, TotpCode, ct);
            Message = $"验证码已发送，{CodeLifetimeMinutes} 分钟内有效。";
            SendCooldownSeconds = emails.CooldownSeconds;
        } catch (EmailCodeRequestException e) {
            Error = e.Message;
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException) {
            if (e.Message is "请输入当前密码。" or "当前密码无效或账号不可用。") PasswordError = e.Message;
            else Error = e.Message;
        }
        CurrentPassword = ""; TotpCode = "";
        ModelState.Remove(nameof(CurrentPassword)); ModelState.Remove(nameof(TotpCode));
        if (Request.Headers["X-Email-Code-Request"] == "true")
            return new JsonResult(new {
                success = Message is not null,
                message = Message ?? Error ?? PasswordError,
                cooldownSeconds = Message is not null ? SendCooldownSeconds : 0,
                errors = PasswordError is not null ? new Dictionary<string, string> { [nameof(CurrentPassword)] = PasswordError } : new Dictionary<string, string>()
            });
        await LoadAccountStateAsync(ct);
        return Page();
    }
    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken ct)
    {
        try {
            await emails.VerifyEmailAsync(Id, EmailAddress, Code, ct);
            Message = "邮箱已绑定。安全设置已更新，请重新登录。"; Bound = true; Mode = null;
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error = e.Message; }
        CurrentPassword = ""; TotpCode = ""; Code = "";
        ModelState.Remove(nameof(CurrentPassword)); ModelState.Remove(nameof(TotpCode)); ModelState.Remove(nameof(Code));
        await LoadAccountStateAsync(ct);
        return Page();
    }
    public async Task<IActionResult> OnPostUnbindAsync(CancellationToken ct)
    {
        Mode = "unbind";
        try {
            await emails.UnbindEmailAsync(Id, CurrentPassword, TotpCode, ct);
            Message = "邮箱已解绑，邮箱验证码登录和找回密码已停用。请重新登录。";
            Mode = null;
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException) {
            if (e.Message is "请输入当前密码。" or "当前密码无效或账号不可用。") PasswordError = e.Message;
            else Error = e.Message;
        }
        CurrentPassword = ""; TotpCode = "";
        ModelState.Remove(nameof(CurrentPassword)); ModelState.Remove(nameof(TotpCode));
        await LoadAccountStateAsync(ct);
        return Page();
    }
    private async Task LoadAccountStateAsync(CancellationToken ct)
    {
        var user = await users.FindByIdAsync(Id, ct);
        Bound = !string.IsNullOrWhiteSpace(user?.Email);
        BoundEmail = user?.Email;
        TotpEnabled = await totp.IsEnabledAsync(Id, ct);
    }
    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        return email[..1] + "***" + (at > 2 ? email[(at - 1)..at] : "") + email[at..];
    }
}
