using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Shared.OAuth;
using static NexusAuth.Host.TokenEndpoint.TokenGrantHelper;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(ITokenGrantStrategy), OAuthGrantTypes.RefreshToken)]
public class RefreshTokenGrantStrategy(
    IRefreshTokenClientStrategyResolver clientStrategies,
    ITokenService tokenService,
    ISecurityPolicyService securityPolicyService,
    IOptions<JwtOptions> jwtOptions)
    : ITokenGrantStrategy
{
    public async Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct)
    {
        var authentication = ParseClientAuthentication(context, jwtOptions.Value.Issuer);
        if (authentication is null)
            return InvalidClient(context.HttpContext);

        var refreshToken = context.Request.RefreshToken;
        // 中文注释：refresh_token 分支会先校验客户端身份，再调用 TokenService 做旧 refresh token 吊销和新 token 轮换。
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient(context.HttpContext);

        var clientStrategy = clientStrategies.Resolve(authentication.ClientId);
        var clientAuthentication = await clientStrategy.AuthenticateAsync(context, authentication, ct);
        if (!clientAuthentication.IsSuccess)
            return InvalidClient(context.HttpContext);

        if (!clientStrategy.IsGrantAllowed(clientAuthentication.Client)
            || !securityPolicyService.CheckClient(authentication.ClientId!).IsSuccess)
            return new BadRequestObjectResult(new { error = "unauthorized_client", error_description = "Client is not allowed to refresh tokens." });

        if (string.IsNullOrWhiteSpace(refreshToken))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "refresh_token is required." });

        var result = await tokenService.RefreshAsync(
            refreshToken,
            authentication.ClientId!,
            ct,
            context.Request.Scope);

        if (!result.IsSuccess)
            return new BadRequestObjectResult(new
            {
                error = result.ErrorCode ?? "invalid_grant",
                error_description = result.Error,
            });

        return new OkObjectResult(new
        {
            access_token = result.AccessToken,
            token_type = "Bearer",
            refresh_token = result.RefreshToken,
            scope = result.Scope,
            expires_in = checked(jwtOptions.Value.AccessTokenLifetimeMinutes * 60),
        });
    }
}
