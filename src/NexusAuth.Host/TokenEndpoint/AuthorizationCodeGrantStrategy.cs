using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Shared.DependencyInjection;
using static NexusAuth.Host.TokenEndpoint.TokenGrantHelper;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(ITokenGrantStrategy), "authorization_code")]
public class AuthorizationCodeGrantStrategy(
    IAuthorizationService authorizationService,
    ITokenService tokenService,
    IClientService clientService,
    IUserService userService,
    ISecurityPolicyService securityPolicyService,
    IOptions<JwtOptions> jwtOptions)
    : ITokenGrantStrategy
{
    public async Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct)
    {
        var authentication = ParseClientAuthentication(context, jwtOptions.Value.Issuer);
        if (authentication is null)
            return InvalidClient(context.HttpContext);

        var code = context.Request.Code;
        var redirectUri = context.Request.RedirectUri;
        var codeVerifier = context.Request.CodeVerifier;
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient(context.HttpContext);

        var clientAuthentication = await clientService.AuthenticateClientAsync(authentication, requireClientAuthentication: true, ct);
        if (!clientAuthentication.IsSuccess)
            return InvalidClient(context.HttpContext);

        if (string.IsNullOrWhiteSpace(code))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "code is required." });

        if (string.IsNullOrWhiteSpace(redirectUri))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "redirect_uri is required." });

        var result = await authorizationService.ValidateAndConsumeCodeAsync(code, authentication.ClientId, redirectUri, codeVerifier, ct);

        if (!result.IsSuccess)
            return new BadRequestObjectResult(new { error = "invalid_grant", error_description = result.Error });

        var user = await userService.FindByIdAsync(result.UserId, ct);
        if (user is not { IsActive: true } || !securityPolicyService.CheckUser(user).IsSuccess)
            return new BadRequestObjectResult(new { error = "invalid_grant", error_description = "The user account is no longer active." });

        TokenIssueResult accessTokenResult;
        string? refreshToken;
        string? idToken = null;
        try
        {
            accessTokenResult = await tokenService.IssueAccessTokenWithMetadataAsync(result.ClientId, result.Scope, null, result.UserId, result.ClaimsJson, ct);
            refreshToken = ShouldIssueRefreshToken(result.Scope)
                ? await tokenService.IssueRefreshTokenAsync(result.ClientId, result.UserId, result.Scope, ct)
                : null;
            var includeIdToken = result.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal);
            if (includeIdToken)
            {
                // 中文注释：授权码里已经保存了 OIDC 上下文，这里在换 token 时继续带到 id_token 里。
                idToken = await tokenService.IssueIdTokenAsync(
                    result.ClientId,
                    result.UserId,
                    result.Nonce,
                    accessTokenResult.AccessToken,
                    result.ClaimsJson,
                    result.AuthenticatedAt,
                    result.Acr,
                    result.Amr,
                    ct);
            }
        }
        catch (InvalidOperationException)
        {
            // 中文注释：授权码已经被消费，签发失败时返回 OAuth 错误 JSON 而不是框架 500。
            return TokenIssuanceFailed();
        }

        return new OkObjectResult(new
        {
            access_token = accessTokenResult.AccessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            scope = result.Scope,
            id_token = idToken,
            expires_in = checked(jwtOptions.Value.AccessTokenLifetimeMinutes * 60),
        });
    }
}
