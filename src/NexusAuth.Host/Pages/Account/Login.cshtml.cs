using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services;
using NexusAuth.Application.Services.LoginAudits;
using NexusAuth.Application.Services.Security;
using NexusAuth.Application.Services.WebAuthn;
using NexusAuth.Application.Users;
using NexusAuth.Host.Authentication;
using NexusAuth.Domain.AggregateRoots.Users;
using NexusAuth.Domain.Entities;
using NexusAuth.Domain.Repositories;
using NexusAuth.Application.Services.Email;

namespace NexusAuth.Host.Pages.Account;

public class LoginModel(
    IUserService userService,
    IWebSignInService webSignInService,
    ILoginAuditService loginAuditService,
    ISecurityPolicyService securityPolicyService,
    ITotpService totpService,
    ISecondFactorRecoveryService recoveryService,
    IOAuthClientRepository clientRepository,
    ILogger<LoginModel> logger,
    ILoginFlowStateProtector flowStateProtector,
    ISliderCaptchaChallengeProtector sliderCaptchaChallengeProtector,
    IOptions<SliderCaptchaOptions> sliderCaptchaOptions,
    IOptions<LoginFlowOptions> flowOptions,
    IOptions<LoginPageOptions> loginPageOptions,
    IOptions<SelfRegistrationOptions> selfRegistrationOptions,
    IFido2 fido2,
    IWebAuthnService webAuthnService,
    IOptions<WebAuthnOptions> webAuthnOptions,
    IEmailAccountService emailAccounts) : PageModel
{
    private readonly LoginFlowOptions _flowOptions = flowOptions.Value;
    private readonly SliderCaptchaOptions _sliderCaptchaOptions = sliderCaptchaOptions.Value;
    private readonly SelfRegistrationOptions _selfRegistrationOptions = selfRegistrationOptions.Value;
    private readonly WebAuthnOptions _webAuthnOptions = webAuthnOptions.Value;

    public LoginPageOptions LoginPage { get; } = loginPageOptions.Value;

    public bool SelfRegistrationEnabled => _selfRegistrationOptions.Enabled;

    public bool PasskeyEnabled => _webAuthnOptions.Enabled;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Mode { get; set; }
    [BindProperty] public string EmailCode { get; set; } = string.Empty;
    public bool EmailLogin => Mode == "email";
    public string? StatusMessage { get; set; }
    public int SendCooldownSeconds { get; private set; }
    public int ConfiguredSendCooldownSeconds => emailAccounts.CooldownSeconds;
    public int CodeLifetimeMinutes => emailAccounts.LifetimeMinutes;

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty]
    public string TotpCode { get; set; } = string.Empty;

    [BindProperty]
    public string FlowToken { get; set; } = string.Empty;

    [BindProperty]
    public string SliderCaptchaToken { get; set; } = string.Empty;

    [BindProperty]
    public int? SliderCaptchaOffset { get; set; }

    public string? ErrorMessage { get; set; }

    public string? AuthorizationClientDisplayName { get; private set; }

    public bool RequiresTotp { get; private set; }

    public bool ShowRememberMe => _flowOptions.AllowRememberMe;

    public int RememberMeLifetimeDays => _flowOptions.RememberMeLifetimeDays;

    public bool SliderCaptchaEnabled => _sliderCaptchaOptions.Enabled;

    public int SliderCaptchaTrackWidthPixels => _sliderCaptchaOptions.TrackWidthPixels;

    public int SliderCaptchaTolerancePixels => _sliderCaptchaOptions.TolerancePixels;

    public string SliderCaptchaImageDataUrl { get; private set; } = string.Empty;
    public SliderCaptchaChallengeTicket? SliderCaptchaImages { get; private set; }

    public IActionResult OnGetCaptcha()
    {
        Response.Headers.CacheControl = "no-store";
        if (!SliderCaptchaEnabled) return NotFound();
        return new JsonResult(sliderCaptchaChallengeProtector.CreateChallenge());
    }

    /// <summary>
    /// 渲染登录页，并清理已有外部认证 Cookie。
    /// </summary>
    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        // If user is already authenticated, redirect back immediately
        if (User.Identity?.IsAuthenticated == true)
        {
            if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
                return Redirect(ReturnUrl);

            return Redirect("/account");
        }

        // Try to extract client_id from returnUrl to show client name
        await TryExtractClientNameAsync();
        RefreshSliderCaptchaChallenge();

        return Page();
    }

    /// <summary>
    /// 提交登录表单，校验账号密码并建立 Cookie 登录会话。
    /// </summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (SliderCaptchaEnabled
            && !sliderCaptchaChallengeProtector.TryValidate(SliderCaptchaToken, SliderCaptchaOffset))
        {
            await RecordLoginAsync(null, false, "SliderCaptchaFailed");
            ErrorMessage = "Please complete the slider verification.";
            Password = string.Empty;
            await TryExtractClientNameAsync();
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Username) || (!EmailLogin && string.IsNullOrWhiteSpace(Password)))
        {
            await RecordLoginAsync(null, false, "MissingCredentials");
            ErrorMessage = "Username and password are required.";
            await TryExtractClientNameAsync();
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        var user = EmailLogin
            ? await emailAccounts.AuthenticateAsync(Username, EmailCode, HttpContext.RequestAborted)
            : await userService.ValidateCredentialsAsync(Username, Password);
        if (user is null)
        {
            await RecordLoginAsync(null, false, "InvalidCredentials");
            ErrorMessage = "Invalid username or password.";
            await TryExtractClientNameAsync();
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        var userPolicy = securityPolicyService.CheckUser(user);
        if (!userPolicy.IsSuccess)
        {
            await RecordLoginAsync(user.Id, false, "UserDeniedByPolicy");
            ErrorMessage = "Invalid username or password.";
            await TryExtractClientNameAsync();
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        var totpStep = _flowOptions.FindStep(LoginFlowStepTypes.Totp);
        if (totpStep is not null)
        {
            var totpEnabled = await totpService.IsEnabledAsync(user.Id, HttpContext.RequestAborted);
            var totpRequired = string.Equals(
                totpStep.Requirement,
                LoginFlowRequirements.Required,
                StringComparison.OrdinalIgnoreCase);
            if (totpRequired && !totpEnabled)
            {
                // An enabled passkey already satisfies the required second
                // factor. Letting a primary factor alone enroll a fresh TOTP
                // here would let anyone holding the password replace it.
                var passkeys = await webAuthnService.GetEnabledCredentialsAsync(user.Id, HttpContext.RequestAborted);
                if (passkeys.Count > 0)
                {
                    await RecordLoginAsync(user.Id, false, "PasskeyRequired");
                    ErrorMessage = PasskeyEnabled
                        ? "This account is protected by a passkey. Sign in with your passkey."
                        : "This account is protected by a passkey, but passkey sign-in is disabled. Contact an administrator.";
                    Password = string.Empty;
                    await TryExtractClientNameAsync();
                    RefreshSliderCaptchaChallenge();
                    return Page();
                }

                return await TotpEnrollmentFlow.StartAsync(HttpContext, user, GetLocalReturnUrl(ReturnUrl),
                    _flowOptions.AllowRememberMe && RememberMe, EmailLogin ? "email" : "pwd",
                    _flowOptions, totpService, flowStateProtector, HttpContext.RequestAborted);
            }

            if (totpEnabled)
            {
                RequiresTotp = true;
                FlowToken = flowStateProtector.Protect(
                    new PendingLoginFlowState(
                        user.Id,
                        user.Username,
                        ReturnUrl,
                        _flowOptions.AllowRememberMe && RememberMe,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        EmailLogin ? "email" : "pwd", TokenEpoch: user.TokenInvalidBefore?.UtcTicks ?? 0),
                    TimeSpan.FromMinutes(_flowOptions.PendingStateLifetimeMinutes));
                Password = string.Empty;
                await TryExtractClientNameAsync();
                return Page();
            }
        }

        return await CompleteLoginAsync(user, RememberMe, DateTimeOffset.UtcNow, EmailLogin ? "email" : "pwd");
    }

    public async Task<IActionResult> OnPostSendEmailCodeAsync()
    {
        Mode = "email";
        if (SliderCaptchaEnabled && !sliderCaptchaChallengeProtector.TryValidate(SliderCaptchaToken, SliderCaptchaOffset))
            ErrorMessage = "Please complete the slider verification.";
        else if (string.IsNullOrWhiteSpace(Username))
            ErrorMessage = "请输入邮箱地址。";
        else
        {
            try
            {
                await emailAccounts.SendPublicAsync(Username, "login", HttpContext.RequestAborted);
                StatusMessage = $"如果邮箱已绑定且账号可用，验证码已发送或存在有效的验证码，请使用已收到的验证码（{CodeLifetimeMinutes} 分钟内有效），不会重复发送。";
                SendCooldownSeconds = emailAccounts.CooldownSeconds;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            { ErrorMessage = e.Message; }
        }
        RefreshSliderCaptchaChallenge();
        await TryExtractClientNameAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTotpAsync()
    {
        RequiresTotp = true;
        if (!flowStateProtector.TryUnprotect(FlowToken, out var flowState) || flowState is null)
        {
            RequiresTotp = false;
            FlowToken = string.Empty;
            ErrorMessage = "The login attempt expired. Start again.";
            return Page();
        }

        Username = flowState.Username;
        ReturnUrl = flowState.ReturnUrl;
        RememberMe = flowState.RememberMe;
        await TryExtractClientNameAsync();

        if (string.IsNullOrWhiteSpace(TotpCode))
        {
            ErrorMessage = "Enter the six-digit authenticator code.";
            return Page();
        }

        var user = await userService.FindByIdAsync(flowState.UserId, HttpContext.RequestAborted);
        if (user is null || !user.IsActive || !securityPolicyService.CheckUser(user).IsSuccess
            || flowState.TokenEpoch is null || flowState.TokenEpoch != (user.TokenInvalidBefore?.UtcTicks ?? 0))
        {
            await RecordLoginAsync(flowState.UserId, false, "UserUnavailable");
            RequiresTotp = false;
            FlowToken = string.Empty;
            ErrorMessage = "The login attempt is no longer valid. Start again.";
            return Page();
        }

        // 中文注释：流程令牌在有效期内可重放，因此每次提交动态码都要先检查持久化锁定状态；
        // 动态码错误由 TotpService 计入同一失败计数，达到阈值后锁定账号。
        if (user.IsLoginLocked(DateTimeOffset.UtcNow))
        {
            await RecordLoginAsync(user.Id, false, "UserLocked");
            RequiresTotp = false;
            FlowToken = string.Empty;
            TotpCode = string.Empty;
            ErrorMessage = "Too many failed attempts. Try again later.";
            RefreshSliderCaptchaChallenge();
            return Page();
        }

        var secondFactorCode = SecondFactorCode.Normalize(TotpCode);
        var isRecoveryCode = !SecondFactorCode.IsTotp(secondFactorCode);
        var factorValid = isRecoveryCode
            ? await recoveryService.RedeemAsync(user.Id, secondFactorCode, HttpContext.RequestAborted)
            : await totpService.ValidateAsync(user.Id, secondFactorCode, HttpContext.RequestAborted);
        if (!factorValid)
        {
            await RecordLoginAsync(user.Id, false, "InvalidTotp");
            TotpCode = string.Empty;
            ErrorMessage = "Invalid or already used authenticator code.";
            return Page();
        }

        // Factor verification reloads the tracked user under the account lock.
        // A reset between the initial flow check and that reload must not make
        // an old primary-factor proof valid against the new user version.
        if (flowState.TokenEpoch != (user.TokenInvalidBefore?.UtcTicks ?? 0))
            return await RestartLoginAsync(SignInInterruptedMessage);

        var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(flowState.AuthenticatedAtUnixSeconds);
        return await CompleteLoginAsync(user, flowState.RememberMe, authenticatedAt,
            flowState.AuthenticationMethods + (isRecoveryCode ? " otp recovery" : " otp"));
    }

    public async Task<IActionResult> OnPostPasskeyOptionsAsync(
        [FromBody] PasskeyAuthenticationOptionsRequest request,
        CancellationToken ct)
    {
        if (!PasskeyEnabled)
            return NotFound();

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            // Empty allowCredentials requests a discoverable credential and
            // lets the platform picker identify the account.
            AllowedCredentials = [],
            UserVerification = TotpIsRequired ? UserVerificationRequirement.Required : _webAuthnOptions.UserVerification,
        });
        var flowToken = await webAuthnService.CreateAuthenticationChallengeAsync(
            JsonSerializer.Serialize(options),
            GetLocalReturnUrl(request.ReturnUrl),
            request.RememberMe && _flowOptions.AllowRememberMe,
            DateTimeOffset.UtcNow.AddSeconds(_webAuthnOptions.ChallengeLifetimeSeconds),
            ct);

        return new JsonResult(new { flowToken, publicKey = options });
    }

    public async Task<IActionResult> OnPostPasskeyVerifyAsync(
        [FromBody] PasskeyAuthenticationVerificationRequest request,
        CancellationToken ct)
    {
        if (!PasskeyEnabled)
            return NotFound();

        var state = await webAuthnService.ConsumeAuthenticationChallengeAsync(
            request.FlowToken,
            DateTimeOffset.UtcNow,
            ct);
        if (state is null)
            return BadRequest(new { error = "invalid_challenge", error_description = "The passkey request expired or was already used." });

        var credential = await webAuthnService.FindCredentialAsync(request.Credential.RawId, ct);
        if (credential is null || !credential.IsEnabled)
            return JsonUnauthorized("This passkey is unavailable.");

        var user = await userService.FindByIdAsync(credential.UserId, ct);
        if (user is null || !user.IsActive || !securityPolicyService.CheckUser(user).IsSuccess)
            return JsonUnauthorized("This passkey is unavailable.");

        // A locked account cannot complete sign-in (WebSignInService refuses
        // to clear an active lockout), so report it before the assertion
        // instead of failing silently afterwards.
        if (user.IsLoginLocked(DateTimeOffset.UtcNow))
        {
            Username = user.Username;
            await RecordLoginAsync(user.Id, false, "UserLocked");
            return JsonUnauthorized(LockedMessage);
        }

        var options = JsonSerializer.Deserialize<AssertionOptions>(state.OptionsJson);
        if (options is null)
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "server_error" });
        if (TotpIsRequired && options.UserVerification != UserVerificationRequirement.Required)
            return JsonUnauthorized("Start a new passkey request with user verification.");

        try
        {
            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = request.Credential,
                OriginalOptions = options,
                StoredPublicKey = credential.PublicKeyCose,
                StoredSignatureCounter = credential.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (input, _) => Task.FromResult(
                    input.UserHandle.SequenceEqual(user.Id.ToByteArray())
                    && input.CredentialId.SequenceEqual(credential.CredentialId)),
            }, ct);

            await webAuthnService.CompleteAuthenticationAsync(
                state,
                credential,
                result.SignCount,
                result.IsBackedUp,
                DateTimeOffset.UtcNow,
                ct);
        }
        catch (Fido2VerificationException exception)
        {
            Username = user.Username;
            await RecordLoginAsync(user.Id, false, "InvalidPasskey");
            return JsonUnauthorized(exception.Message);
        }
        catch (InvalidOperationException)
        {
            Username = user.Username;
            await RecordLoginAsync(user.Id, false, "PasskeyCounterRegression");
            return JsonUnauthorized("The passkey could not be verified.");
        }

        ReturnUrl = state.ReturnUrl;
        Username = user.Username;
        var resultRedirect = await CompleteLoginAsync(user, state.RememberMe, DateTimeOffset.UtcNow,
            options.UserVerification == UserVerificationRequirement.Required ? "webauthn uv" : "webauthn");
        if (resultRedirect is not RedirectResult redirect)
            return JsonUnauthorized(ErrorMessage ?? SignInInterruptedMessage);
        return new JsonResult(new { redirectUrl = redirect.Url });
    }

    private async Task<IActionResult> CompleteLoginAsync(
        User user,
        bool rememberMe,
        DateTimeOffset authenticatedAt,
        string authenticationMethods)
    {
        Username = user.Username;
        try
        {
            await webSignInService.SignInAsync(HttpContext, user, rememberMe, authenticatedAt,
                authenticationMethods, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            await RecordLoginAsync(user.Id, false, "AuthenticationStateChanged");
            return await RestartLoginAsync(SignInInterruptedMessage);
        }
        await RecordLoginAsync(user.Id, true, null);

        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return Redirect(ReturnUrl);

        return Redirect("/account");
    }

    private const string LockedMessage = "Too many failed attempts. Try again later.";
    private const string SignInInterruptedMessage =
        "The account was locked or its security settings changed during sign-in. Sign in again.";

    /// <summary>
    /// Returns to the first login step with an explanation. The bound
    /// ReturnUrl is kept so a pending OIDC authorization request survives.
    /// </summary>
    private async Task<IActionResult> RestartLoginAsync(string message)
    {
        RequiresTotp = false;
        FlowToken = string.Empty;
        TotpCode = string.Empty;
        Password = string.Empty;
        ErrorMessage = message;
        await TryExtractClientNameAsync();
        RefreshSliderCaptchaChallenge();
        return Page();
    }

    private async Task TryExtractClientNameAsync()
    {
        if (string.IsNullOrWhiteSpace(ReturnUrl))
            return;

        try
        {
            // Parse returnUrl to extract client_id query parameter
            var uri = new Uri(ReturnUrl, UriKind.RelativeOrAbsolute);
            string? query;

            if (uri.IsAbsoluteUri)
                query = uri.Query;
            else
            {
                // For relative URIs, prepend a dummy base to parse query string
                var absolute = new Uri(new Uri("http://localhost"), ReturnUrl);
                query = absolute.Query;
            }

            if (string.IsNullOrEmpty(query))
                return;

            var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
            if (!queryParams.TryGetValue("client_id", out var clientId) || string.IsNullOrWhiteSpace(clientId))
                return;

            var client = await clientRepository.FindByClientIdAsync(clientId.ToString(), HttpContext.RequestAborted);
            AuthorizationClientDisplayName = client is { IsActive: true } ? client.ClientName : null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to resolve authorization client name for the login page.");
            AuthorizationClientDisplayName = null;
        }
    }

    private Task RecordLoginAsync(Guid? userId, bool isSuccessful, string? failureReason)
    {
        var clientId = TryExtractClientId();
        return loginAuditService.RecordAsync(new LoginAuditRecord(
            Username,
            userId,
            clientId,
            isSuccessful,
            failureReason,
            ClientIpConfiguration.GetAddress(HttpContext),
            HttpContext.Request.Headers.UserAgent.ToString()), HttpContext.RequestAborted);
    }

    private void RefreshSliderCaptchaChallenge()
    {
        ModelState.Remove(nameof(SliderCaptchaToken));
        ModelState.Remove(nameof(SliderCaptchaOffset));

        if (!SliderCaptchaEnabled)
        {
            SliderCaptchaToken = string.Empty;
            SliderCaptchaOffset = null;
            return;
        }

        var challenge = sliderCaptchaChallengeProtector.CreateChallenge();
        SliderCaptchaImages = challenge;
        SliderCaptchaToken = challenge.Token;
        SliderCaptchaImageDataUrl = challenge.ImageDataUrl;
        SliderCaptchaOffset = null;
    }

    private string? TryExtractClientId()
    {
        if (string.IsNullOrWhiteSpace(ReturnUrl))
            return null;

        try
        {
            var uri = new Uri(new Uri("http://localhost"), ReturnUrl);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            return query.TryGetValue("client_id", out var clientId) ? clientId.ToString() : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private string? GetLocalReturnUrl(string? returnUrl) => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
        ? returnUrl
        : null;

    private bool TotpIsRequired => string.Equals(_flowOptions.FindStep(LoginFlowStepTypes.Totp)?.Requirement,
        LoginFlowRequirements.Required, StringComparison.OrdinalIgnoreCase);

    private static JsonResult JsonUnauthorized(string description) => new(
        new { error = "invalid_credential", error_description = description })
    {
        StatusCode = StatusCodes.Status401Unauthorized,
    };
}
