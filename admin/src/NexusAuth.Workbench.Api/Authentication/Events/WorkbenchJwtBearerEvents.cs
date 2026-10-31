using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api.Authentication.Events;

/// <summary>在 SDK 的 Access Token 与客户端校验之后，追加 Workbench 的用户撤销状态检查。</summary>
public sealed class WorkbenchJwtBearerEvents(
    IOptions<NexusAuthClientOptions> clientOptions,
    IUserTokenValidator tokenValidator) : NexusAuthJwtBearerEvents(clientOptions)
{
    // JWT 尚未过期也要检查用户禁用、会话撤销、token_epoch 和 jti 黑名单。
    protected override Task<bool> IsActiveAsync(TokenValidatedContext context) =>
        tokenValidator.IsActiveAsync(context.Principal!, context.HttpContext.RequestAborted);
}
