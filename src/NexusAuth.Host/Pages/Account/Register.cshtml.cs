using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Host.Authentication;

namespace NexusAuth.Host.Pages.Account;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class RegisterModel(
    IUserService userService,
    IWebSignInService webSignInService,
    IAntiforgery antiforgery,
    WebAuthnEnrollmentStateProtector enrollmentStateProtector,
    SliderCaptchaChallengeProtector sliderCaptchaChallengeProtector,
    IOptions<SliderCaptchaOptions> sliderCaptchaOptions,
    IOptions<SelfRegistrationOptions> selfRegistrationOptions,
    IOptions<WebAuthnOptions> webAuthnOptions) : PageModel
{
    private readonly SelfRegistrationOptions selfRegistration = selfRegistrationOptions.Value;
    private readonly WebAuthnOptions webAuthn = webAuthnOptions.Value;
    private readonly SliderCaptchaOptions sliderCaptcha = sliderCaptchaOptions.Value;

    [BindProperty]
    [Required(ErrorMessage = "请输入登录账号。")]
    [StringLength(64, MinimumLength = 3, ErrorMessage = "登录账号长度应为 3 到 64 个字符。")]
    [RegularExpression("^[a-zA-Z0-9._-]+$", ErrorMessage = "登录账号只能包含字母、数字、点、下划线和连字符。")]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请输入用户名。")]
    [StringLength(64, MinimumLength = 2, ErrorMessage = "用户名长度应为 2 到 64 个字符。")]
    public string Nickname { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请输入邮箱地址。")]
    [EmailAddress(ErrorMessage = "请输入有效的邮箱地址。")]
    [StringLength(256, ErrorMessage = "邮箱地址不能超过 256 个字符。")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请输入密码。")]
    [StringLength(PasswordPolicy.MaximumUtf8Bytes, MinimumLength = PasswordPolicy.MinimumLength, ErrorMessage = "密码长度应为 8 到 72 个字符。")]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "请再次输入密码。")]
    [Compare(nameof(Password), ErrorMessage = "两次输入的密码不一致。")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string SliderCaptchaToken { get; set; } = string.Empty;

    [BindProperty]
    public int? SliderCaptchaOffset { get; set; }

    public bool SliderCaptchaEnabled => sliderCaptcha.Enabled;

    public int SliderCaptchaTrackWidthPixels => sliderCaptcha.TrackWidthPixels;

    public string SliderCaptchaImageDataUrl { get; private set; } = string.Empty;
    public SliderCaptchaChallengeTicket? SliderCaptchaImages { get; private set; }

    public IActionResult OnGetCaptcha()
    {
        Response.Headers.CacheControl = "no-store";
        if (!SliderCaptchaEnabled) return NotFound();
        return new JsonResult(sliderCaptchaChallengeProtector.CreateChallenge());
    }

    public string? ErrorMessage { get; private set; }

    public bool SelfRegistrationEnabled => selfRegistration.Enabled;

    public IActionResult OnGet()
    {
        if (!SelfRegistrationEnabled)
            return NotFound();

        if (User.Identity?.IsAuthenticated == true)
            return Redirect("/account");

        ReturnUrl = GetLocalReturnUrl(ReturnUrl);
        RefreshSliderCaptchaChallenge();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!SelfRegistrationEnabled)
            return NotFound();

        ReturnUrl = GetLocalReturnUrl(ReturnUrl);

        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return BadRequest();
        }

        if (SliderCaptchaEnabled
            && !sliderCaptchaChallengeProtector.TryValidate(SliderCaptchaToken, SliderCaptchaOffset))
        {
            ErrorMessage = "请完成拼图验证。";
            ClearPasswords();
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        // 中文注释：与找回密码、修改密码、管理员重置共用同一密码策略（BCrypt 只使用前 72 个 UTF-8 字节）。
        if (ModelState.GetFieldValidationState(nameof(Password)) != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid
            && PasswordPolicy.GetValidationError(Password) is { } passwordError)
            ModelState.AddModelError(nameof(Password), passwordError);

        if (!ModelState.IsValid)
        {
            ClearPasswords();
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        try
        {
            var userId = await userService.RegisterAsync(
                Username.Trim(),
                Password,
                Nickname.Trim(),
                Email.Trim(),
                ct: ct);

            var user = await userService.FindByIdAsync(userId, ct)
                ?? throw new InvalidOperationException("The registered account could not be loaded.");

            // 注册表单已经验证了用户设置的密码，因此直接建立登录会话，无需再次输入密码。
            await webSignInService.SignInAsync(
                HttpContext,
                user,
                rememberMe: false,
                DateTimeOffset.UtcNow,
                authenticationMethods: "pwd",
                ct);

            // Passkey 关闭时账号注册已经完成，直接回到原授权流程或账号页。
            if (!webAuthn.Enabled)
                return GetPostRegistrationRedirect();

            var enrollmentToken = enrollmentStateProtector.Protect(
                new WebAuthnEnrollmentState(userId, ReturnUrl),
                TimeSpan.FromMinutes(15));
            return RedirectToPage("/Account/PasskeyEnrollment", new { token = enrollmentToken });
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = exception.Message;
        }

        ClearPasswords();
        RefreshSliderCaptchaChallenge();
        return Page();
    }

    private string? GetLocalReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : null;
    }

    private IActionResult GetPostRegistrationRedirect() =>
        !string.IsNullOrWhiteSpace(ReturnUrl)
            ? Redirect(ReturnUrl)
            : Redirect("/account");

    private void ClearPasswords()
    {
        var passwordErrors = ModelState[nameof(Password)]?.Errors.Select(error => error.ErrorMessage).ToArray() ?? [];
        var confirmationErrors = ModelState[nameof(ConfirmPassword)]?.Errors.Select(error => error.ErrorMessage).ToArray() ?? [];
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        ModelState.Remove(nameof(Password));
        ModelState.Remove(nameof(ConfirmPassword));

        foreach (var error in passwordErrors)
            ModelState.AddModelError(nameof(Password), error);

        foreach (var error in confirmationErrors)
            ModelState.AddModelError(nameof(ConfirmPassword), error);
    }

    private void RefreshSliderCaptchaChallenge()
    {
        ModelState.Remove(nameof(SliderCaptchaToken));
        ModelState.Remove(nameof(SliderCaptchaOffset));

        if (!SliderCaptchaEnabled)
        {
            SliderCaptchaToken = string.Empty;
            SliderCaptchaOffset = null;
            SliderCaptchaImageDataUrl = string.Empty;
            return;
        }

        var challenge = sliderCaptchaChallengeProtector.CreateChallenge();
        SliderCaptchaImages = challenge;
        SliderCaptchaToken = challenge.Token;
        SliderCaptchaOffset = null;
        SliderCaptchaImageDataUrl = challenge.ImageDataUrl;
    }
}
