using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Workbench.Api.Options;

namespace NexusAuth.Workbench.Api.Authentication.Events;

/// <summary>在框架完成 JWT 验签后，校验 Workbench 客户端信任和用户撤销状态。</summary>
public sealed class WorkbenchJwtBearerEvents(
    IOptions<WorkbenchAuthenticationOptions> authenticationOptions,
    IUserTokenValidator tokenValidator) : JwtBearerEvents
{
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal;
        // ID Token 的用途是登录，不能把它作为访问 API 的 Access Token。
        if (principal?.FindFirstValue("token_use") != "access_token")
        {
            context.Fail("Only access tokens are accepted.");
            return;
        }

        var settings = authenticationOptions.Value;
        var clientId = principal.FindFirstValue("client_id");
        if (string.IsNullOrWhiteSpace(clientId)
            || !(string.Equals(clientId, settings.ClientId, StringComparison.Ordinal)
                || settings.AllowedBearerClientIds.Contains(clientId, StringComparer.Ordinal)))
        {
            context.Fail("The token client is not allowed.");
            return;
        }

        // JWT 尚未过期也要检查用户禁用、会话撤销、token_epoch 和 jti 黑名单。
        if (!await tokenValidator.IsActiveAsync(principal, context.HttpContext.RequestAborted))
        {
            context.Fail("The token is inactive.");
            return;
        }

        // 保留 Access Token 原始 claim，兼容以 NameIdentifier 取当前用户的现有 API。
        if (principal.Identity is ClaimsIdentity identity
            && !identity.HasClaim(claim => claim.Type == ClaimTypes.NameIdentifier)
            && principal.FindFirstValue("sub") is { } subject)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
    }
}
