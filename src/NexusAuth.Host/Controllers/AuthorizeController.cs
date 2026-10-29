using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services;
using NexusAuth.Domain.Repositories;

namespace NexusAuth.Host.Controllers;

[ApiController]
public class AuthorizeController(
    IAuthorizationService authorizationService,
    IClientService clientService,
    IDataProtectionProvider? dataProtectionProvider = null,
    IUserService? userService = null,
    ISecurityPolicyService? securityPolicyService = null,
    IOptions<JwtOptions>? jwtOptions = null,
    IConsentStore? consentStore = null)
    : ControllerBase
{
    /// <summary>
    /// 中文注释：强制重新认证（prompt=login / max_age 超时）时附加到 returnUrl 的内部参数。
    /// 值是受 DataProtection 保护、带有效期的“要求重新认证的时间点”，登录回来后只要 auth_time
    /// 不早于该时间点即视为已重新认证，避免 prompt=login / max_age=0 无限循环登录。
    /// </summary>
    public const string ReauthenticationParameterName = "nexusauth_reauth";
    public const string ConsentParameterName = "nexusauth_consent";

    private const string ReauthenticationProtectorPurpose = "NexusAuth.Authorize.Reauthentication.v1";
    private static readonly TimeSpan ReauthenticationMarkerLifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _reauthenticationProtector =
        (dataProtectionProvider ?? new EphemeralDataProtectionProvider())
            .CreateProtector(ReauthenticationProtectorPurpose)
            .ToTimeLimitedDataProtector();

    /// <summary>
    /// OAuth2.0 Authorization Endpoint.
    /// Supports response_type=code (Authorization Code flow with optional PKCE).
    /// 
    /// If the user is already authenticated (via cookie), the authorization code is issued
    /// immediately and the user is redirected back to the client — no login page shown.
    /// 
    /// If the user is NOT authenticated, they are redirected to /account/login with the
    /// current URL as returnUrl. After login, they are redirected back here with a valid cookie.
    /// </summary>
    /// <summary>
    /// OAuth2/OIDC 授权端点，负责校验请求并签发 authorization code。
    /// 主要流程：
    /// 1. 校验 client_id / redirect_uri / scope / PKCE
    /// 2. 检查当前浏览器是否已登录
    /// 3. 必要时跳转登录页或 consent 页
    /// 4. 登录成功后签发 authorization code 并重定向回客户端
    /// 主要调用方：
    /// - Demo.Bff
    /// - Demo.Bff.ClientSecret
    /// - 任意标准 OIDC Web Client
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    [HttpGet("/connect/authorize")]
    public async Task<IActionResult> Authorize(
        [FromQuery(Name = "response_type")] string? responseType,
        [FromQuery(Name = "client_id")] string? clientId,
        [FromQuery(Name = "redirect_uri")] string? redirectUri,
        [FromQuery] string? scope,
        [FromQuery] string? state = null,
        [FromQuery] string? nonce = null,
        [FromQuery] string? prompt = null,
        [FromQuery(Name = "max_age")] int? maxAge = null,
        [FromQuery(Name = "response_mode")] string? responseMode = null,
        [FromQuery] string? claims = null,
        [FromQuery(Name = "code_challenge")] string? codeChallenge = null,
        [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return AuthorizationError("invalid_request", "client_id is required.");

        if (string.IsNullOrWhiteSpace(redirectUri))
            return AuthorizationError("invalid_request", "redirect_uri is required.");

        // Establish the trusted callback boundary before any redirect. Invalid client or
        // redirect_uri requests are always handled locally and never become open redirects.
        var redirectValidation = await clientService.ValidateClientRedirectUriAsync(clientId, redirectUri, ct);

        if (!redirectValidation.IsSuccess)
        {
            return AuthorizationError(
                redirectValidation.ErrorCode ?? "invalid_request",
                redirectValidation.Error ?? "The authorization request is invalid.");
        }

        var normalizedResponseMode = ResolveResponseMode(responseMode);
        if (normalizedResponseMode is null)
        {
            return AuthorizationError(
                redirectUri,
                "query",
                "invalid_request",
                "response_mode must be query or form_post.",
                state);
        }

        if (Request.Query.ContainsKey("request") || Request.Query.ContainsKey("request_uri"))
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                Request.Query.ContainsKey("request_uri") ? "request_uri_not_supported" : "request_not_supported",
                "Request objects and request_uri are not supported.",
                state);
        }

        if (string.IsNullOrWhiteSpace(responseType))
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "invalid_request",
                "response_type is required.",
                state);
        }

        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "unsupported_response_type",
                "Only 'code' response type is supported.",
                state);
        }

        var clientValidation = await clientService.ValidateClientForAuthorizationAsync(
            clientId, redirectUri, "authorization_code", codeChallenge, codeChallengeMethod, ct);

        if (!clientValidation.IsSuccess)
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                clientValidation.ErrorCode ?? "invalid_request",
                clientValidation.Error ?? "The authorization request is invalid.",
                state);
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "invalid_request",
                "scope is required.",
                state);
        }

        var scopeValidation = await clientService.ValidateScopesAsync(clientId, scope, allowIdentityScopes: true, ct);
        if (!scopeValidation.IsSuccess)
        {
            if (string.Equals(scopeValidation.ErrorCode, "invalid_client", StringComparison.Ordinal))
                return AuthorizationError("invalid_client", scopeValidation.Error ?? "Client not found or inactive.");

            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                scopeValidation.ErrorCode ?? "invalid_scope",
                scopeValidation.Error ?? "The requested scope is invalid.",
                state);
        }

        var promptValues = ParsePrompt(prompt);
        var unsupportedPrompt = promptValues.FirstOrDefault(value =>
            !value.Equals("none", StringComparison.Ordinal)
            && !value.Equals("login", StringComparison.Ordinal)
            && !value.Equals("consent", StringComparison.Ordinal));
        if (unsupportedPrompt is not null)
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "invalid_request",
                $"Unsupported prompt value '{unsupportedPrompt}'.",
                state);
        }

        if (maxAge is < 0)
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "invalid_request",
                "max_age must not be negative.",
                state);
        }

        if (!string.IsNullOrWhiteSpace(claims))
        {
            try
            {
                // 中文注释：OIDC claims 参数允许客户端声明希望返回哪些 claim，这里先做格式校验。
                authorizationService.ParseRequestedClaims(claims);
            }
            catch (InvalidOperationException ex)
            {
                return AuthorizationError(redirectUri, normalizedResponseMode, "invalid_request", ex.Message, state);
            }
        }

        if (promptValues.Contains("none", StringComparer.Ordinal) && promptValues.Count > 1)
        {
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "invalid_request",
                "prompt=none cannot be combined with other prompt values.",
                state);
        }

        // Check if user is already authenticated via cookie
        if (User.Identity?.IsAuthenticated != true)
        {
            if (promptValues.Contains("none", StringComparer.Ordinal))
            {
                return AuthorizationError(
                    redirectUri,
                    normalizedResponseMode,
                    "login_required",
                    "User authentication is required.",
                    state);
            }

            // Not logged in — redirect to login page with returnUrl (must be relative for Url.IsLocalUrl)
            // 中文注释：未登录时接下来的登录本身就是一次全新认证；带 prompt=login / max_age 的请求附上
            // 重新认证标记，登录回来后不会再被要求登录第二次。
            var returnUrl = promptValues.Contains("login", StringComparer.Ordinal) || maxAge.HasValue
                ? BuildCurrentReturnUrl(CreateReauthenticationMarker())
                : Request.GetEncodedPathAndQuery();
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var authenticatedAt = GetAuthenticatedAt();
        var reauthenticationMarker = Request.Query[ReauthenticationParameterName].ToString();
        var reauthenticated = IsReauthenticationSatisfied(reauthenticationMarker, authenticatedAt);

        if (promptValues.Contains("login", StringComparer.Ordinal) && !reauthenticated)
        {
            // 中文注释：prompt=login 要求强制用户重新登录，即使当前已经有 Cookie 会话。
            // 登录回来后携带受保护的重新认证标记，auth_time 晚于标记即放行，不会再次触发登录。
            return await ForceReauthenticationAsync();
        }

        if (maxAge.HasValue && !reauthenticated && !IsWithinMaxAge(authenticatedAt, maxAge.Value))
        {
            if (promptValues.Contains("none", StringComparer.Ordinal))
            {
                return AuthorizationError(
                    redirectUri,
                    normalizedResponseMode,
                    "login_required",
                    "The current session is too old for max_age.",
                    state);
            }

            return await ForceReauthenticationAsync();
        }

        // User is authenticated — extract user ID from cookie claims
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return AuthorizationError("server_error", "Unable to identify the authenticated user.");

        if (!await IsUserAllowedAsync(userId, ct))
        {
            // 中文注释：已有 Cookie 会话的用户也要满足当前的账号状态与安全策略（黑/白名单）。
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "access_denied",
                "The user account is not allowed to authorize this client.",
                state);
        }

        var consentId = Request.Query[ConsentParameterName].ToString();
        var hasOfflineAccess = scopeValidation.NormalizedScope!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("offline_access", StringComparer.Ordinal);
        var requiresConsent = clientValidation.Client?.RequireConsent == true || hasOfflineAccess;
        var forceConsent = promptValues.Contains("consent", StringComparer.Ordinal) || hasOfflineAccess;
        var originalQuery = QueryString.Create(Request.Query
            .Where(pair => !string.Equals(pair.Key, ConsentParameterName, StringComparison.Ordinal))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))).Value ?? "";

        if (!string.IsNullOrWhiteSpace(consentId))
        {
            if (consentStore is null || Request.Query[ConsentParameterName].Count != 1)
                return AuthorizationError(redirectUri, normalizedResponseMode, "invalid_request", "Invalid consent request.", state);

            var pending = await consentStore.GetRequestAsync(consentId, userId, ct);
            if (pending is null || !string.Equals(pending.Query, originalQuery, StringComparison.Ordinal))
                return AuthorizationError(redirectUri, normalizedResponseMode, "invalid_request", "Invalid consent request.", state);

            var approved = await consentStore.ConsumeApprovedRequestAsync(consentId, userId, ct);
            if (approved is null || !string.Equals(approved.Query, originalQuery, StringComparison.Ordinal))
                return AuthorizationError(redirectUri, normalizedResponseMode, "invalid_request", "Consent has expired or was already used.", state);

            if (approved.Remember)
                await consentStore.SaveGrantAsync(userId, clientId, scopeValidation.NormalizedScope!, claims, ct);
        }
        else if (requiresConsent || forceConsent)
        {
            if (consentStore is null)
                return AuthorizationError(redirectUri, normalizedResponseMode, "server_error", "Consent is unavailable.", state);

            var previouslyGranted = !forceConsent && await consentStore.HasGrantAsync(
                userId, clientId, scopeValidation.NormalizedScope!, claims, ct);
            if (!previouslyGranted)
            {
                if (promptValues.Contains("none", StringComparer.Ordinal))
                    return AuthorizationError(redirectUri, normalizedResponseMode, "consent_required", "User consent is required.", state);

                var requestId = await consentStore.CreateRequestAsync(userId, originalQuery, ct);
                return Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/consent", "id", requestId));
            }
        }

        // Generate authorization code
        string code;
        try
        {
            code = await authorizationService.GenerateCodeAsync(
                userId,
                clientId,
                redirectUri,
                scopeValidation.NormalizedScope!,
                codeChallenge,
                codeChallengeMethod,
                nonce,
                claims,
                authenticatedAt,
                User.FindFirst("acr")?.Value,
                User.FindFirst("amr")?.Value,
                ct);
        }
        catch (InvalidOperationException)
        {
            // Scope and claims were validated above. A concurrent client change can still
            // invalidate them between validation and persistence; return an OAuth error
            // instead of leaking a framework 500 response.
            return AuthorizationError(
                redirectUri,
                normalizedResponseMode,
                "invalid_scope",
                "The requested scope is no longer valid.",
                state);
        }

        var parameters = new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = state,
        };

        return BuildAuthorizationResponse(redirectUri, normalizedResponseMode, parameters);
    }

    private DateTimeOffset? GetAuthenticatedAt()
    {
        var authTimeClaim = User.FindFirst("auth_time")?.Value;
        if (long.TryParse(authTimeClaim, out var unixTimeSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);

        return null;
    }

    /// <summary>
    /// 中文注释：auth_time 只精确到秒，按整秒比较 max_age（OIDC Core §3.1.2.1）。
    /// </summary>
    private static bool IsWithinMaxAge(DateTimeOffset? authenticatedAt, int maxAge)
    {
        if (!authenticatedAt.HasValue)
            return false;

        var elapsedSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - authenticatedAt.Value.ToUnixTimeSeconds();
        return elapsedSeconds <= maxAge;
    }

    private bool IsReauthenticationSatisfied(string? marker, DateTimeOffset? authenticatedAt)
    {
        if (string.IsNullOrWhiteSpace(marker) || !authenticatedAt.HasValue)
            return false;

        try
        {
            var payload = _reauthenticationProtector.Unprotect(marker);
            return long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requiredAfter)
                && authenticatedAt.Value.ToUnixTimeSeconds() >= requiredAfter;
        }
        catch (CryptographicException)
        {
            // 中文注释：被篡改或已过期的标记一律视为未重新认证。
            return false;
        }
    }

    private async Task<IActionResult> ForceReauthenticationAsync()
    {
        await HttpContext.SignOutAsync(AppWebModule.AuthenticationScheme);

        var returnUrl = BuildCurrentReturnUrl(CreateReauthenticationMarker());
        return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    private string CreateReauthenticationMarker()
    {
        return _reauthenticationProtector.Protect(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ReauthenticationMarkerLifetime);
    }

    /// <summary>
    /// 中文注释：重建当前授权请求的本地 URL，并替换（或移除）内部的重新认证标记参数。
    /// </summary>
    private string BuildCurrentReturnUrl(string? reauthenticationMarker)
    {
        var parameters = Request.Query
            .Where(pair => !string.Equals(pair.Key, ReauthenticationParameterName, StringComparison.Ordinal))
            .ToList();

        if (!string.IsNullOrWhiteSpace(reauthenticationMarker))
            parameters.Add(new KeyValuePair<string, StringValues>(ReauthenticationParameterName, reauthenticationMarker));

        var path = (Request.PathBase + Request.Path).ToString();
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            string.IsNullOrEmpty(path) ? "/connect/authorize" : path,
            parameters);
    }

    private async Task<bool> IsUserAllowedAsync(Guid userId, CancellationToken ct)
    {
        if (userService is null)
            return true;

        var user = await userService.FindByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
            return false;

        return securityPolicyService?.CheckUser(user).IsSuccess ?? true;
    }

    private static HashSet<string> ParsePrompt(string? prompt)
    {
        return prompt?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
    }

    private static string? ResolveResponseMode(string? responseMode)
    {
        if (string.IsNullOrWhiteSpace(responseMode)
            || string.Equals(responseMode, "query", StringComparison.Ordinal))
            return "query";

        return string.Equals(responseMode, "form_post", StringComparison.Ordinal)
            ? "form_post"
            : null;
    }

    private IActionResult AuthorizationError(
        string error,
        string description)
    {
        return RedirectToPage("/OAuthError", new
        {
            error,
            error_description = description,
        });
    }

    private IActionResult AuthorizationError(
        string redirectUri,
        string? responseMode,
        string error,
        string description,
        string? state)
    {
        return BuildAuthorizationResponse(
            redirectUri,
            responseMode ?? "query",
            new Dictionary<string, string?>
            {
                ["error"] = error,
                ["error_description"] = description,
                ["state"] = state,
            });
    }

    private IActionResult BuildAuthorizationResponse(
        string redirectUri,
        string responseMode,
        IReadOnlyDictionary<string, string?> parameters)
    {
        var presentParameters = parameters
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(jwtOptions?.Value.Issuer))
            presentParameters["iss"] = jwtOptions.Value.Issuer.TrimEnd('/');

        if (!string.Equals(responseMode, "form_post", StringComparison.Ordinal))
        {
            var redirectUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                redirectUri,
                presentParameters);
            return new RedirectResult(redirectUrl);
        }

        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        var action = System.Net.WebUtility.HtmlEncode(redirectUri);
        var hiddenInputs = string.Join(
            Environment.NewLine,
            presentParameters.Select(pair =>
                $"<input type=\"hidden\" name=\"{System.Net.WebUtility.HtmlEncode(pair.Key)}\" value=\"{System.Net.WebUtility.HtmlEncode(pair.Value)}\" />"));
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Submitting authorization response</title></head><body><form method=\"post\" action=\"{action}\">{hiddenInputs}<noscript><button type=\"submit\">Continue</button></noscript></form><script>document.forms[0].submit();</script></body></html>";
        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = StatusCodes.Status200OK,
        };
    }

}
