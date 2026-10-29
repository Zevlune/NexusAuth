using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api.Authorization;

namespace NexusAuth.Workbench.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    IFlowStateStore flows,
    INexusAuthClient client,
    WorkbenchAuthenticationSettings settings,
    IOptions<WorkbenchFlowStateOptions> flowOptions,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpGet("/api/auth/config")]
    [AllowAnonymous]
    public IActionResult Config() => Ok(new
    {
        authority = settings.Authority,
        clientId = settings.ClientId,
        redirectUri = settings.RedirectUri,
    });

    [HttpGet("/api/auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("workbench-login")]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        var flow = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var binding = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await flows.AddAsync(flow, new FlowState(WorkbenchLoginFlow.Hash(binding)), ct);
        }
        catch (FlowStateCapacityExceededException)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "too_many_login_requests" });
        }

        var cookie = WorkbenchLoginFlow.CookieOptions(HttpContext, cookieOptions, flowOptions.Value.Lifetime);
        Response.Cookies.Append(WorkbenchLoginFlow.CookieName(flow, cookie.Secure), binding, cookie);
        var properties = new AuthenticationProperties
        {
            RedirectUri = settings.PostLogoutRedirectUri.TrimEnd('/') + "/auth/callback",
            IsPersistent = true,
            AllowRefresh = true,
        };
        properties.Items[WorkbenchLoginFlow.PropertyKey] = flow;
        try
        {
            await HttpContext.ChallengeAsync(WorkbenchAuthenticationDefaults.OpenIdConnectScheme, properties);
            var authorizeUrl = Response.Headers.Location.ToString();
            if (string.IsNullOrEmpty(authorizeUrl))
                throw new InvalidOperationException("The provider did not supply an authorization URL.");
            Response.Headers.Remove("Location");
            Response.StatusCode = StatusCodes.Status200OK;
            return Ok(new { authorizeUrl });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await flows.RemoveAsync(flow, CancellationToken.None);
            Response.Headers.Remove("Set-Cookie");
            Response.Headers.Remove("Location");
            logger.LogWarning("Provider discovery failed during login. FailureType={FailureType}", exception.GetType().Name);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "provider_unavailable" });
        }
    }

    // The standard OpenID Connect handler owns /signin-oidc and validates its state, PKCE and nonce.

    [HttpPost("/api/auth/logout")]
    [Authorize(Policy = WorkbenchAuthorizationPolicies.AuthenticatedUser)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var logoutUrl = string.Empty;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var tokenName in new[] { "refresh_token", "access_token" })
            {
                var token = await HttpContext.GetTokenAsync(WorkbenchAuthenticationDefaults.CookieScheme, tokenName);
                if (string.IsNullOrEmpty(token)) continue;
                try { await client.RevokeTokenAsync(token, tokenName, timeout.Token); }
                catch (Exception exception)
                {
                    logger.LogWarning("Provider token revocation failed during logout. TokenType={TokenType} FailureType={FailureType}",
                        tokenName, exception.GetType().Name);
                }
            }
            if (settings.SignOutProvider
                && !string.IsNullOrEmpty(await HttpContext.GetTokenAsync(WorkbenchAuthenticationDefaults.CookieScheme, "id_token")))
            {
                try
                {
                    await client.FetchConfigurationAsync(timeout.Token);
                    await HttpContext.SignOutAsync(WorkbenchAuthenticationDefaults.OpenIdConnectScheme,
                        new AuthenticationProperties { RedirectUri = settings.PostLogoutRedirectUri });
                    logoutUrl = Response.Headers.Location.ToString();
                }
                catch (Exception exception)
                {
                    logger.LogWarning("Provider sign-out failed. FailureType={FailureType}", exception.GetType().Name);
                }
            }
        }
        finally
        {
            await HttpContext.SignOutAsync(WorkbenchAuthenticationDefaults.CookieScheme);
        }
        Response.Headers.Remove("Location");
        Response.StatusCode = StatusCodes.Status200OK;
        return Ok(new { logoutUrl });
    }
}
