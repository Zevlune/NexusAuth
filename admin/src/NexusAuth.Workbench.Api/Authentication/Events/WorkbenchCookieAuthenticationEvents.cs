using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NexusAuth.Workbench.Api.Authentication.Tokens;
using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Options;

namespace NexusAuth.Workbench.Api.Authentication.Events;

/// <summary>
/// 校验 Workbench 会话有效性，并在令牌临近过期时刷新受保护票据中的令牌。
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
            // 未过期也需要内省：管理员撤销会话或禁用用户后，不允许继续凭旧 Cookie 访问。
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

            // 并发请求可能复用刚完成的刷新结果；再次内省，避免复用管理员刚撤销的令牌。
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

            // 同时更新本应用会话窗口与票据，保持 Cookie 滑动续期和令牌刷新一致。
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
