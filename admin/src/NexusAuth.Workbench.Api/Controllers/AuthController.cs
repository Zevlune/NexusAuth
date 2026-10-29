using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
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
    OpenIdConnectFlowState flows,
    IWorkbenchTokenClient client,
    IOptions<WorkbenchAuthenticationOptions> authenticationOptions,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    ILogger<AuthController> logger) : ControllerBase
{
    private readonly WorkbenchAuthenticationOptions settings = authenticationOptions.Value;

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
        var properties = new AuthenticationProperties
        {
            RedirectUri = settings.PostLogoutRedirectUri.TrimEnd('/') + "/auth/callback",
            IsPersistent = true,
            AllowRefresh = true,
        };
        try
        {
            await flows.BeginAsync(HttpContext, properties, WorkbenchAuthenticationDefaults.OpenIdConnectScheme, ct);
            await HttpContext.ChallengeAsync(WorkbenchAuthenticationDefaults.OpenIdConnectScheme, properties);
            var authorizeUrl = Response.Headers.Location.ToString();
            if (string.IsNullOrEmpty(authorizeUrl))
                throw new InvalidOperationException("The provider did not supply an authorization URL.");
            Response.Headers.Remove("Location");
            Response.StatusCode = StatusCodes.Status200OK;
            return Ok(new { authorizeUrl });
        }
        catch (FlowStateCapacityExceededException)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "too_many_login_requests" });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await flows.CancelAsync(HttpContext, properties, WorkbenchAuthenticationDefaults.OpenIdConnectScheme, CancellationToken.None);
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
                    // Prime the handler's own cache within the shared logout budget.
                    await oidcOptions.Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme).ConfigurationManager!
                        .GetConfigurationAsync(timeout.Token).WaitAsync(timeout.Token);
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
