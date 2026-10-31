using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>
/// 校验会话有效性，并在 Access Token 临近过期时刷新受保护票据中的令牌。
/// 刷新经 <see cref="INexusAuthTokenRefreshCoordinator"/> 合并，集群部署时可切换为 Redis 协调。
/// </summary>
public class NexusAuthCookieAuthenticationEvents(
    INexusAuthTokenClient tokenClient,
    IOptions<NexusAuthClientOptions> clientOptions,
    INexusAuthTokenRefreshCoordinator refreshCoordinator) : CookieAuthenticationEvents
{
    private readonly NexusAuthClientOptions settings = clientOptions.Value;

    /// <inheritdoc />
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

        if (expiresAt > DateTimeOffset.UtcNow.Add(settings.Session.RefreshBeforeExpiry))
        {
            if (!settings.Session.ValidateWithIntrospection)
                return;
            // 未过期也需要内省：管理员撤销会话或禁用用户后，不允许继续凭旧 Cookie 访问。
            try
            {
                if (!await IsActiveAsync(context, accessToken))
                    await RejectAndSignOutAsync(context);
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
            if (settings.Session.ValidateWithIntrospection && !await IsActiveAsync(context, refreshed.AccessToken))
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

            // 同时更新会话窗口与票据，保持 Cookie 滑动续期和令牌刷新一致。
            var now = DateTimeOffset.UtcNow;
            context.Properties.IssuedUtc = now;
            context.Properties.ExpiresUtc = now.Add(settings.Session.Lifetime);
            context.Properties.AllowRefresh = true;
            context.ShouldRenew = true;
        }
        catch
        {
            await RejectAndSignOutAsync(context);
        }
    }

    private async Task<bool> IsActiveAsync(CookieValidatePrincipalContext context, string accessToken)
    {
        var introspection = await tokenClient.IntrospectAccessTokenAsync(
            accessToken, context.HttpContext.RequestAborted);
        return introspection.Active
            && string.Equals(introspection.Subject, context.Principal!.FindFirstValue(ClaimTypes.NameIdentifier), StringComparison.Ordinal)
            && string.Equals(introspection.ClientId, settings.ClientId, StringComparison.Ordinal);
    }

    private static async Task RejectAndSignOutAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }
}
