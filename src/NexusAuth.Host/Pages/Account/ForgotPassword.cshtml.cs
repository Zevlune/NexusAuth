using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NexusAuth.Host.Authentication;
using NexusAuth.Application.Services.Email;
using Microsoft.Extensions.Options;

namespace NexusAuth.Host.Pages.Account;

[AllowAnonymous]
public sealed class ForgotPasswordModel(IEmailAccountService emails,
    ISliderCaptchaChallengeProtector captcha, IOptions<SliderCaptchaOptions> options) : PageModel
{
    [BindProperty] public string EmailAddress { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    [BindProperty] public string TotpCode { get; set; } = "";
    [BindProperty] public string SliderCaptchaToken { get; set; } = "";
    [BindProperty] public int? SliderCaptchaOffset { get; set; }
    public bool SliderCaptchaEnabled => options.Value.Enabled;
    public int SliderCaptchaTrackWidthPixels => options.Value.TrackWidthPixels;
    public SliderCaptchaChallengeTicket? Images { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public int SendCooldownSeconds { get; private set; }
    public int ConfiguredSendCooldownSeconds => emails.CooldownSeconds;
    public int CodeLifetimeMinutes => emails.LifetimeMinutes;
    public void OnGet() => Prepare();
    public IActionResult OnGetCaptcha()
    {
        Response.Headers.CacheControl = "no-store";
        return SliderCaptchaEnabled ? new JsonResult(captcha.CreateChallenge()) : NotFound();
    }
    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        if (!ValidateCaptcha()) { Prepare(); return Page(); }
        try {
            await emails.SendPublicAsync(EmailAddress, "reset-password", ct);
            Message = $"如果邮箱已绑定且账号可用，验证码已发送或存在有效的验证码，请使用已收到的验证码（{CodeLifetimeMinutes} 分钟内有效），不会重复发送。";
            SendCooldownSeconds = emails.CooldownSeconds;
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error = e.Message; }
        Prepare(); return Page();
    }
    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        if (!ValidateCaptcha()) { Prepare(); return Page(); }
        try {
            if (NewPassword != ConfirmPassword) throw new ArgumentException("两次新密码不一致。");
            await emails.ResetPasswordAsync(EmailAddress, Code, NewPassword, TotpCode, ct);
            Message = "密码已重置，现有登录与令牌已撤销，请返回登录。";
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error = e.Message; }
        Prepare(); return Page();
    }
    private bool ValidateCaptcha()
    {
        if (!SliderCaptchaEnabled || captcha.TryValidate(SliderCaptchaToken, SliderCaptchaOffset)) return true;
        Error = "请完成拼图验证。"; return false;
    }
    private void Prepare()
    {
        Response.Headers.CacheControl = "no-store";
        NewPassword = ConfirmPassword = "";
        ModelState.Remove(nameof(NewPassword)); ModelState.Remove(nameof(ConfirmPassword));
        ModelState.Remove(nameof(SliderCaptchaToken)); ModelState.Remove(nameof(SliderCaptchaOffset));
        if (SliderCaptchaEnabled) { Images = captcha.CreateChallenge(); SliderCaptchaToken = Images.Token; }
    }
}
