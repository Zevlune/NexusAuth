using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api;

/// <summary>
/// Keeps the Workbench authentication ticket alive while rotating its OIDC tokens.
/// </summary>
public class WorkbenchCookieAuthenticationEvents(
    IWorkbenchTokenClient tokenClient,
    IOptions<WorkbenchAuthenticationOptions> authenticationOptions,
    IWorkbenchTokenRefreshCoordinator refreshCoordinator) : CookieAuthenticationEvents
{
    private readonly WorkbenchAuthenticationOptions settings = authenticationOptions.Value;

    private static readonly TimeSpan AccessTokenRefreshWindow = TimeSpan.FromMinutes(1);
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null)
        {
            await RejectAndSignOutAsync(context);
            return;
        }

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        var accessToken = context.Properties.GetTokenValue("access_token");
        var expiresAtValue = context.Properties.GetTokenValue("expires_at");
        if (string.IsNullOrWhiteSpace(accessToken)
            || string.IsNullOrWhiteSpace(refreshToken)
            || string.IsNullOrWhiteSpace(expiresAtValue))
        {
            await RejectAndSignOutAsync(context);
            return;
        }

        if (!DateTimeOffset.TryParse(
                expiresAtValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAt))
        {
            await RejectAndSignOutAsync(context);
            return;
        }

        if (expiresAt > DateTimeOffset.UtcNow.Add(AccessTokenRefreshWindow))
        {
            try
            {
                var introspection = await tokenClient.IntrospectAccessTokenAsync(
                    accessToken,
                    context.HttpContext.RequestAborted);
                var subject = context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!introspection.Active
                    || !string.Equals(introspection.Subject, subject, StringComparison.Ordinal)
                    || !string.Equals(introspection.ClientId, settings.ClientId, StringComparison.Ordinal))
                {
                    await RejectAndSignOutAsync(context);
                }
            }
            catch
            {
                await RejectAndSignOutAsync(context);
            }
            return;
        }

        try
        {
            var refreshed = await refreshCoordinator.RefreshAsync(
                refreshToken,
                context.HttpContext.RequestAborted);
            var idToken = refreshed.IdToken ?? context.Properties.GetTokenValue("id_token");
            var nextRefreshToken = refreshed.RefreshToken ?? refreshToken;
            if (string.IsNullOrWhiteSpace(refreshed.AccessToken)
                || refreshed.ExpiresIn <= 0
                || string.IsNullOrWhiteSpace(nextRefreshToken)
                || string.IsNullOrWhiteSpace(idToken))
            {
                throw new InvalidOperationException("The refresh response did not contain the required tokens.");
            }

            // A completed refresh may be reused briefly by concurrent requests.
            // Recheck its persisted state in case an administrator revoked the
            // user after that result was produced.
            var introspection = await tokenClient.IntrospectAccessTokenAsync(
                refreshed.AccessToken, context.HttpContext.RequestAborted);
            if (!introspection.Active
                || !string.Equals(introspection.Subject, context.Principal.FindFirstValue(ClaimTypes.NameIdentifier), StringComparison.Ordinal)
                || !string.Equals(introspection.ClientId, settings.ClientId, StringComparison.Ordinal))
                throw new InvalidOperationException("The refreshed token is no longer active.");

            var refreshedExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
            var tokens = context.Properties.GetTokens()
                .Where(token => token.Name is not ("access_token" or "refresh_token" or "id_token" or "expires_at"))
                .Concat(
                [
                    new AuthenticationToken { Name = "access_token", Value = refreshed.AccessToken },
                    new AuthenticationToken { Name = "refresh_token", Value = nextRefreshToken },
                    new AuthenticationToken { Name = "id_token", Value = idToken },
                    new AuthenticationToken { Name = "expires_at", Value = refreshedExpiresAt.ToString("O", CultureInfo.InvariantCulture) },
                ]);
            context.Properties.StoreTokens(tokens);

            // Renew the independent Workbench session window together with the
            // ticket so both cookie sliding and token sliding remain active.
            var now = DateTimeOffset.UtcNow;
            context.Properties.IssuedUtc = now;
            context.Properties.ExpiresUtc = now.AddHours(24);
            context.Properties.AllowRefresh = true;
            context.ShouldRenew = true;
        }
        catch
        {
            await RejectAndSignOutAsync(context);
        }
    }

    private static async Task RejectAndSignOutAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(WorkbenchAuthenticationDefaults.CookieScheme);
    }
}
