using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Shared.DependencyInjection;
using static NexusAuth.Host.TokenEndpoint.TokenGrantHelper;

namespace NexusAuth.Host.TokenEndpoint;

[KeyedService(typeof(ITokenGrantStrategy), "urn:ietf:params:oauth:grant-type:device_code")]
public class DeviceCodeGrantStrategy(
    IDeviceAuthorizationService deviceAuthorizationService,
    ITokenService tokenService,
    IUserService userService,
    IOptions<JwtOptions> jwtOptions)
    : ITokenGrantStrategy
{
    public async Task<IActionResult> ExecuteAsync(TokenGrantContext context, CancellationToken ct)
    {
        var authentication = ParseClientAuthentication(context, jwtOptions.Value.Issuer);
        if (authentication is null)
            return InvalidClient(context.HttpContext);

        var deviceCode = context.Request.DeviceCode;
        // 中文注释：device_code 分支本身只负责“兑换”，真正的 device_code 状态机在 DeviceAuthorizationService 中维护。
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient(context.HttpContext);

        if (string.IsNullOrWhiteSpace(deviceCode))
            return new BadRequestObjectResult(new { error = "invalid_request", error_description = "device_code is required." });

        var result = await deviceAuthorizationService.PollAsync(authentication, deviceCode, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "invalid_client")
                return InvalidClient(context.HttpContext);

            if (result.ErrorCode == "invalid_request")
                return new BadRequestObjectResult(new { error = "invalid_request", error_description = result.Error });

            if (result.ErrorCode is "unauthorized_client" or "access_denied")
                return new BadRequestObjectResult(new { error = result.ErrorCode, error_description = result.Error });

            if (result.ErrorCode is "authorization_pending" or "slow_down" or "expired_token")
                return new BadRequestObjectResult(new { error = result.ErrorCode, error_description = result.Error, interval = result.Interval > 0 ? (int?)result.Interval : null });

            return new BadRequestObjectResult(new { error = "invalid_grant", error_description = result.Error });
        }

        if ((await userService.FindByIdAsync(result.UserId, ct))?.IsActive != true)
            return new BadRequestObjectResult(new { error = "invalid_grant", error_description = "The user account is no longer active." });

        var accessTokenResult = await tokenService.IssueAccessTokenWithMetadataAsync(result.ClientId!, result.Scope!, null, result.UserId, null, ct);
        var refreshToken = ShouldIssueRefreshToken(result.Scope!)
            ? await tokenService.IssueRefreshTokenAsync(result.ClientId!, result.UserId, result.Scope!, ct)
            : null;
        var includeIdToken = result.Scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal);
        string? idToken = null;
        if (includeIdToken)
            idToken = await tokenService.IssueIdTokenAsync(result.ClientId!, result.UserId, null, accessTokenResult.AccessToken, ct: ct);

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
