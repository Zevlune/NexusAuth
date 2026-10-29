using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Demo.Bff.Models;
using Demo.Bff.Options;
using Demo.Bff.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Shared.OAuth;

namespace Demo.Bff.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
public class AuthController : ControllerBase
{
    /// <summary>
    /// 登录流程（state / nonce / code_verifier）的有效期，超时未回调的流程会被清理。
    /// </summary>
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, OidcFlowState> _flowStore;
    private readonly OidcBffService _oidcBffService;
    private readonly BffSessionOptions _sessionOptions;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ConcurrentDictionary<string, OidcFlowState> flowStore,
        OidcBffService oidcBffService,
        IOptions<BffSessionOptions> sessionOptions,
        ILogger<AuthController> logger)
    {
        _flowStore = flowStore;
        _oidcBffService = oidcBffService;
        _sessionOptions = sessionOptions.Value;
        _logger = logger;
    }

    [HttpGet("/api/config")]
    [AllowAnonymous]
    public IActionResult Config()
    {
        return Ok(new
        {
            authority = _oidcBffService.Authority,
            clientId = _oidcBffService.ClientId,
        });
    }

    [HttpGet("/api/auth/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        var discovery = await _oidcBffService.FetchDiscoveryAsync(ct);
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var pkce = _oidcBffService.GeneratePkce();
        var now = DateTimeOffset.UtcNow;
        var expiresAtUtc = now.Add(FlowLifetime);

        RemoveExpiredFlows(now);
        _flowStore[state] = new OidcFlowState(pkce.CodeVerifier, nonce, expiresAtUtc);

        // 将 state 绑定到发起登录的浏览器：回调时必须带回相同的 Cookie，
        // 防止攻击者把自己账号的授权码/state 注入受害者浏览器完成登录（Login CSRF）。
        // SameSite=Lax 保证从 NexusAuth 顶级跳转回 /signin-oidc 时 Cookie 仍会发送。
        Response.Cookies.Append(_sessionOptions.StateCookieName, state, CreateStateCookieOptions(expiresAtUtc));

        var authorizeUrl = discovery.AuthorizationEndpoint +
                           $"?response_type={OAuthResponseTypes.Code}" +
                           $"&client_id={Uri.EscapeDataString(_oidcBffService.ClientId)}" +
                           $"&redirect_uri={Uri.EscapeDataString(_oidcBffService.RedirectUri)}" +
                           $"&scope={Uri.EscapeDataString(_oidcBffService.Scope)}" +
                           $"&state={Uri.EscapeDataString(state)}" +
                           $"&nonce={Uri.EscapeDataString(nonce)}" +
                           $"&code_challenge={Uri.EscapeDataString(pkce.CodeChallenge)}" +
                           $"&code_challenge_method={PkceMethods.S256}";

        return Ok(new { authorizeUrl });
    }

    [HttpGet("/signin-oidc")]
    [AllowAnonymous]
    public async Task<IActionResult> SignInOidc(CancellationToken ct)
    {
        var code = Request.Query["code"].ToString();
        var state = Request.Query["state"].ToString();
        var error = Request.Query["error"].ToString();
        var boundState = Request.Cookies[_sessionOptions.StateCookieName];
        Response.Cookies.Delete(_sessionOptions.StateCookieName, CreateStateCookieOptions(expiresAtUtc: null));

        // 只有当回调 state 与本浏览器的绑定 Cookie 一致时才消费对应的登录流程。
        OidcFlowState? flow = null;
        var hasValidFlow = !string.IsNullOrWhiteSpace(state)
            && IsStateBoundToBrowser(state, boundState)
            && _flowStore.TryRemove(state, out flow)
            && flow.ExpiresAtUtc > DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(error))
            return Redirect($"{_oidcBffService.FrontendBaseUrl}/login?error={Uri.EscapeDataString(error)}");

        if (string.IsNullOrWhiteSpace(code) || !hasValidFlow || flow is null)
            return Redirect($"{_oidcBffService.FrontendBaseUrl}/login?error=invalid_callback");

        var discovery = await _oidcBffService.FetchDiscoveryAsync(ct);
        var httpClient = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        await _oidcBffService.ApplyClientAuthenticationAsync(httpClient);
        var tokenForm = new Dictionary<string, string>
        {
            ["grant_type"] = OAuthGrantTypes.AuthorizationCode,
            ["code"] = code,
            ["redirect_uri"] = _oidcBffService.RedirectUri,
            ["code_verifier"] = flow.CodeVerifier,
        };
        await _oidcBffService.AppendClientAuthenticationFormFieldsAsync(tokenForm, discovery.TokenEndpoint, ct);
        var tokenRequest = new FormUrlEncodedContent(tokenForm);

        var tokenResponse = await httpClient.PostAsync(discovery.TokenEndpoint, tokenRequest, ct);
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
        if (!tokenResponse.IsSuccessStatusCode)
            return Redirect($"{_oidcBffService.FrontendBaseUrl}/login?error=token_exchange_failed");

        using var tokenDocument = JsonDocument.Parse(tokenJson);
        var root = tokenDocument.RootElement;
        var accessToken = root.GetProperty("access_token").GetString();
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshTokenElement) ? refreshTokenElement.GetString() : null;
        var idToken = root.TryGetProperty("id_token", out var idTokenElement) ? idTokenElement.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresInElement) ? expiresInElement.GetInt32() : 3600;

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(idToken))
            return Redirect($"{_oidcBffService.FrontendBaseUrl}/login?error=missing_tokens");

        ValidatedIdToken validatedIdToken;
        try
        {
            validatedIdToken = await _oidcBffService.ValidateIdTokenAsync(discovery, idToken, flow.Nonce, ct);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            // 签名/issuer/aud/过期/nonce 校验失败或令牌格式错误：返回前端错误码，而不是 500。
            _logger.LogWarning(ex, "OIDC id_token validation failed.");
            return Redirect($"{_oidcBffService.FrontendBaseUrl}/login?error=invalid_id_token");
        }

        var userInfo = await _oidcBffService.FetchUserInfoAsync(discovery.UserInfoEndpoint, accessToken, ct);

        var claims = new List<Claim>
        {
            new("sub", validatedIdToken.Subject ?? string.Empty),
            new("name", userInfo.Name ?? validatedIdToken.Name ?? validatedIdToken.PreferredUsername ?? validatedIdToken.Subject ?? "Unknown"),
            new("preferred_username", userInfo.PreferredUsername ?? validatedIdToken.PreferredUsername ?? string.Empty),
            new("session_payload", JsonSerializer.Serialize(new SessionPayload(accessToken, refreshToken, idToken, expiresIn, userInfo))),
        };

        if (!string.IsNullOrWhiteSpace(userInfo.Email))
            claims.Add(new("email", userInfo.Email));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(_sessionOptions.CookieLifetimeMinutes),
        });

        return Redirect($"{_oidcBffService.FrontendBaseUrl}/auth/callback");
    }

    [HttpGet("/api/auth/me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var session = _oidcBffService.ReadSession(User);
        if (session is null)
            return Unauthorized();

        return Ok(new
        {
            isAuthenticated = true,
            user = session.User,
            expiresIn = session.ExpiresIn,
        });
    }

    // 允许匿名：即使会话已无法校验（例如续签时 NexusAuth 不可用），也能删除本地 Cookie。
    [HttpPost("/api/auth/logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var session = _oidcBffService.ReadSession(User);

        // 先清除本地会话，保证即使 NexusAuth 不可用，本地 Cookie 也一定会被删除。
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (session is not null)
        {
            // 尽力吊销令牌：失败只记录日志，不影响本地登出。
            try
            {
                var discovery = await _oidcBffService.FetchDiscoveryAsync(ct);
                await _oidcBffService.RevokeIfPresentAsync(discovery.RevocationEndpoint, session.AccessToken, "access_token", ct);
                await _oidcBffService.RevokeIfPresentAsync(
                    discovery.RevocationEndpoint,
                    session.RefreshToken,
                    OAuthGrantTypes.RefreshToken,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Token revocation during logout failed; local session was already cleared.");
            }
        }

        return Ok(new
        {
            logoutUrl = $"{_oidcBffService.Authority.TrimEnd('/')}/connect/endsession?id_token_hint={Uri.EscapeDataString(session?.IdToken ?? string.Empty)}&post_logout_redirect_uri={Uri.EscapeDataString(_oidcBffService.PostLogoutRedirectUri)}&state={Uri.EscapeDataString(Guid.NewGuid().ToString("N"))}",
        });
    }

    private CookieOptions CreateStateCookieOptions(DateTimeOffset? expiresAtUtc)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Path = "/",
            IsEssential = true,
            Expires = expiresAtUtc,
        };
    }

    private static bool IsStateBoundToBrowser(string state, string? boundState)
    {
        if (string.IsNullOrEmpty(boundState))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(state),
            Encoding.UTF8.GetBytes(boundState));
    }

    /// <summary>
    /// 清理超时未完成的登录流程，避免内存中的 state 无限增长。
    /// </summary>
    private void RemoveExpiredFlows(DateTimeOffset now)
    {
        foreach (var (state, flow) in _flowStore)
        {
            if (flow.ExpiresAtUtc <= now)
                _flowStore.TryRemove(state, out _);
        }
    }
}
