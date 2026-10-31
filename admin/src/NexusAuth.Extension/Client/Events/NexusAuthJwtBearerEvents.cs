using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>
/// 在框架完成 JWT 验签后，只接受 Access Token 且限定调用客户端。
/// 需要额外检查（例如用户禁用、黑名单）的应用可继承并覆盖 <see cref="IsActiveAsync"/>。
/// </summary>
public class NexusAuthJwtBearerEvents(IOptions<NexusAuthClientOptions> clientOptions) : JwtBearerEvents
{
    /// <inheritdoc />
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal;
        // ID Token 的用途是登录，不能把它作为访问 API 的 Access Token。
        if (principal?.FindFirstValue("token_use") != "access_token")
        {
            context.Fail("Only access tokens are accepted.");
            return;
        }

        var settings = clientOptions.Value;
        var clientId = principal.FindFirstValue("client_id");
        if (string.IsNullOrWhiteSpace(clientId)
            || !(string.Equals(clientId, settings.ClientId, StringComparison.Ordinal)
                || settings.AllowedBearerClientIds.Contains(clientId, StringComparer.Ordinal)))
        {
            context.Fail("The token client is not allowed.");
            return;
        }

        if (!await IsActiveAsync(context))
        {
            context.Fail("The token is inactive.");
            return;
        }

        // 保留 Access Token 原始 claim，同时补入 NameIdentifier，与 Cookie 会话取当前用户的方式一致。
        if (principal.Identity is ClaimsIdentity identity
            && !identity.HasClaim(claim => claim.Type == ClaimTypes.NameIdentifier)
            && principal.FindFirstValue("sub") is { } subject)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
    }

    /// <summary>JWT 尚未过期时的附加有效性检查，默认通过。</summary>
    protected virtual Task<bool> IsActiveAsync(TokenValidatedContext context) => Task.FromResult(true);
}
