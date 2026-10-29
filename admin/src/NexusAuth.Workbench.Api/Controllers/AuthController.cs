using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using NexusAuth.Workbench.Api.Authorization;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IFlowStateStore _flowStore;
    private readonly IOidcWorkbenchService _oidcService;
    private readonly WorkbenchFlowStateOptions _flowOptions;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IFlowStateStore flowStore, IOidcWorkbenchService oidcService,
        IOptions<WorkbenchFlowStateOptions> flowOptions,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, ILogger<AuthController> logger)
    {
        _flowStore = flowStore;
        _oidcService = oidcService;
        _flowOptions = flowOptions.Value;
        _cookieOptions = cookieOptions;
        _logger = logger;
    }

    [HttpGet("/api/auth/config")]
    [AllowAnonymous]
    public IActionResult Config()
    {
        return Ok(new
        {
            authority = _oidcService.Authority,
            clientId = _oidcService.ClientId,
            redirectUri = _oidcService.RedirectUri,
        });
    }

    [HttpGet("/api/auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("workbench-login")]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var correlation = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string codeChallenge;
        string codeVerifier;
        (codeChallenge, codeVerifier) = _oidcService.GeneratePkce();

        try
        {
            await _flowStore.AddAsync(state, new FlowState(codeVerifier, nonce, HashBinding(correlation)), ct);
        }
        catch (FlowStateCapacityExceededException)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "too_many_login_requests" });
        }

        DiscoveryDocument discovery;
        try { discovery = await _oidcService.FetchDiscoveryAsync(ct); }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await _flowStore.RemoveAsync(state, CancellationToken.None);
            _logger.LogWarning("Provider discovery failed during login. FailureType={FailureType}", exception.GetType().Name);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "provider_unavailable" });
        }
        Response.Cookies.Append(CorrelationCookieName(state), correlation, CorrelationCookieOptions());

        var authorizeUrl = discovery.AuthorizationEndpoint +
            $"?response_type={OAuthResponseTypes.Code}" +
            $"&client_id={Uri.EscapeDataString(_oidcService.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(_oidcService.RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(_oidcService.Scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
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

        var frontendBase = _oidcService.PostLogoutRedirectUri.TrimEnd('/');

        if (state.Length != 64 || !state.All(Uri.IsHexDigit)
            || !Request.Cookies.TryGetValue(CorrelationCookieName(state), out var correlation)
            || correlation.Length != 64)
            return Redirect($"{frontendBase}/login?error=invalid_callback");

        var flow = await _flowStore.TryTakeAsync(state, HashBinding(correlation), ct);
        if (flow == null)
            return Redirect($"{frontendBase}/login?error=invalid_callback");

        Response.Cookies.Delete(CorrelationCookieName(state), CorrelationCookieOptions());
        if (!string.IsNullOrWhiteSpace(error))
            return RedirectToProviderError(error, Request.Query["error_description"].ToString());
        if (string.IsNullOrWhiteSpace(code))
            return Redirect($"{frontendBase}/login?error=invalid_callback");

        try
        {
            var tokenResult = await _oidcService.ExchangeCodeAsync(code, flow.CodeVerifier, ct);
            var idToken = tokenResult.IdToken
                ?? throw new InvalidOperationException("The authorization response did not contain an ID token.");
            var discovery = await _oidcService.FetchDiscoveryAsync(ct);
            var validatedIdToken = await _oidcService.ValidateIdTokenAsync(discovery, idToken, flow.Nonce, ct);
            var principal = WorkbenchPrincipalFactory.Create(validatedIdToken);
            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.AddSeconds(tokenResult.ExpiresIn);
            var properties = new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = true,
                IssuedUtc = now,
                // The Workbench session lifetime is independent from the short-lived access token.
                ExpiresUtc = now.AddHours(24),
            };
            properties.StoreTokens(
            [
                new AuthenticationToken { Name = "access_token", Value = tokenResult.AccessToken },
                new AuthenticationToken { Name = "refresh_token", Value = tokenResult.RefreshToken },
                new AuthenticationToken { Name = "id_token", Value = idToken },
                new AuthenticationToken { Name = "expires_at", Value = expiresAt.ToString("O") },
            ]);

            await HttpContext.SignInAsync(WorkbenchAuthenticationDefaults.CookieScheme, principal, properties);

            return Redirect($"{frontendBase}/auth/callback");
        }
        catch
        {
            return Redirect($"{frontendBase}/login?error=token_exchange_failed");
        }
    }

    private string CorrelationCookieName(string state) =>
        $"{(UseSecureCookies ? "__Host-" : ".")}NexusAuth.Workbench.Oidc.{state}";

    private bool UseSecureCookies => Request.IsHttps || _cookieOptions.Get(WorkbenchAuthenticationDefaults.CookieScheme)
        .Cookie.SecurePolicy == CookieSecurePolicy.Always;

    private CookieOptions CorrelationCookieOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = UseSecureCookies,
        Path = "/",
        MaxAge = _flowOptions.Lifetime,
        IsEssential = true,
    };

    private static string HashBinding(string correlation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(correlation)));

    private IActionResult RedirectToProviderError(string error, string? description)
    {
        var providerErrorUrl = $"{_oidcService.Authority.TrimEnd('/')}/oauth/error?error={Uri.EscapeDataString(error)}";
        if (!string.IsNullOrWhiteSpace(description))
            providerErrorUrl += $"&error_description={Uri.EscapeDataString(description)}";

        return Redirect(providerErrorUrl);
    }

    [HttpPost("/api/auth/logout")]
    [Authorize(Policy = WorkbenchAuthorizationPolicies.AuthenticatedUser)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var logoutUrl = string.Empty;
        var idToken = await HttpContext.GetTokenAsync(
            WorkbenchAuthenticationDefaults.CookieScheme,
            "id_token") ?? User.FindFirstValue("id_token");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var tokenName in new[] { "refresh_token", "access_token" })
            {
                var token = await HttpContext.GetTokenAsync(WorkbenchAuthenticationDefaults.CookieScheme, tokenName);
                if (string.IsNullOrWhiteSpace(token)) continue;
                try { await _oidcService.RevokeTokenAsync(token, tokenName, timeout.Token); }
                catch (Exception exception)
                {
                    _logger.LogWarning("Provider token revocation failed during logout. TokenType={TokenType} FailureType={FailureType}",
                        tokenName, exception.GetType().Name);
                }
            }
            if (_oidcService.SignOutProvider && !string.IsNullOrWhiteSpace(idToken))
            {
                try
                {
                    var discovery = await _oidcService.FetchDiscoveryAsync(timeout.Token);
                    if (!string.IsNullOrWhiteSpace(discovery.EndSessionEndpoint))
                    {
                        logoutUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(discovery.EndSessionEndpoint,
                            new Dictionary<string, string?>
                            {
                                ["id_token_hint"] = idToken,
                                ["post_logout_redirect_uri"] = _oidcService.PostLogoutRedirectUri,
                            });
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning("Provider discovery failed during logout. FailureType={FailureType}", exception.GetType().Name);
                }
            }
        }
        finally
        {
            await HttpContext.SignOutAsync(WorkbenchAuthenticationDefaults.CookieScheme);
        }

        return Ok(new { logoutUrl });
    }
}
