using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services;
using NexusAuth.Application.Services.ExternalIdentity;
using NexusAuth.Domain.Repositories;
using System.Text;

namespace NexusAuth.Host.Controllers;

[ApiController]
public class TokenController(
    IAuthorizationService authorizationService,
    ITokenService tokenService,
    IDeviceAuthorizationService deviceAuthorizationService,
    IClientService clientService,
    IUserService userService,
    ISecurityPolicyService securityPolicyService,
    IOptions<JwtOptions> jwtOptions,
    IWeChatMiniProgramLoginService weChatMiniProgramLoginService,
    IOptions<WeChatMiniProgramOptions> weChatOptions,
    IUserRepository userRepository) : ControllerBase
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;
    private readonly WeChatMiniProgramOptions _weChatOptions = weChatOptions.Value;

    /// <summary>
    /// OAuth2.0 Token Endpoint.
    /// Supports grant_type: authorization_code, client_credentials, refresh_token,
    /// device_code and the configured WeChat mini program extension grant.
    /// </summary>
    /// <summary>
    /// 统一 Token 入口，根据 grant_type 分发到具体流程。
    /// 主要流程：
    /// 1. 解析客户端认证（client_secret_basic / client_secret_post / private_key_jwt）
    /// 2. 进入 authorization_code / client_credentials / refresh_token / device_code 分支
    /// 3. 调用 Application 层完成客户端校验、授权码消费、refresh token 轮换或 device_code 兑换
    /// 4. 签发 access_token / id_token / refresh_token
    /// 主要调用方：
    /// - Demo.Bff
    /// - Demo.Bff.ClientSecret
    /// - Demo.ClientCredentials
    /// - Demo.DeviceCode
    /// - Demo.RefreshToken
    /// </summary>
    [HttpPost("/connect/token")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(
        [FromForm(Name = "grant_type")] string? grantType,
        [FromForm(Name = "client_id")] string? clientId = null,
        [FromForm(Name = "client_secret")] string? clientSecret = null,
        [FromForm] string? code = null,
        [FromForm(Name = "redirect_uri")] string? redirectUri = null,
        [FromForm(Name = "code_verifier")] string? codeVerifier = null,
        [FromForm] string? scope = null,
        [FromForm(Name = "refresh_token")] string? refreshToken = null,
        [FromForm(Name = "device_code")] string? deviceCode = null,
        [FromForm(Name = "client_assertion_type")] string? clientAssertionType = null,
        [FromForm(Name = "client_assertion")] string? clientAssertion = null,
        [FromForm] string? nickname = null,
        CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        if (string.IsNullOrWhiteSpace(grantType))
            return BadRequest(new { error = "invalid_request", error_description = "grant_type is required." });

        if (string.Equals(grantType, WeChatMiniProgramGrantTypes.GrantType, StringComparison.Ordinal))
        {
            // 微信小程序是 public client，不能安全保存 client_secret。它使用微信一次性
            // code 作为上游认证材料，只有这个扩展 grant 允许省略 OAuth client secret。
            if (clientSecret is not null
                || clientAssertionType is not null
                || clientAssertion is not null
                || !string.IsNullOrWhiteSpace(Request.Headers.Authorization))
            {
                return InvalidClient();
            }

            // Preserve explicitly submitted whitespace for validation; form model
            // binding otherwise converts it to null, which means "keep nickname".
            nickname ??= Request.Form["nickname"].FirstOrDefault();
            return await HandleWeChatMiniProgramAsync(clientId, code, scope, nickname, ct);
        }

        // 中文注释：统一解析 OAuth2 客户端认证，优先兼容标准的 client_secret_basic，
        // 同时保留现有 demo 在用的 client_secret_post 表单方式。
        // 主要调用方：Demo.Bff、移动端 demo，以及任意标准 OAuth2/OIDC 客户端。
        var resolvedClientAuthentication = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            Request.Headers.Authorization.ToString(),
            clientId,
            clientSecret,
            clientAssertionType,
            clientAssertion,
            GetEndpointAudience());

        if (!resolvedClientAuthentication.IsSuccess || resolvedClientAuthentication.Authentication is null)
            return InvalidClient();

        var authentication = resolvedClientAuthentication.Authentication;

        return grantType switch
        {
            "authorization_code" => await HandleAuthorizationCodeAsync(authentication, code, redirectUri, codeVerifier, ct),
            "client_credentials" => await HandleClientCredentialsAsync(authentication, scope, ct),
            "refresh_token" => await HandleRefreshTokenAsync(authentication, refreshToken, ct),
            "urn:ietf:params:oauth:grant-type:device_code" => await HandleDeviceCodeAsync(authentication, deviceCode, ct),
            _ => BadRequest(new { error = "unsupported_grant_type", error_description = $"Grant type '{grantType}' is not supported." }),
        };
    }

    private async Task<IActionResult> HandleWeChatMiniProgramAsync(
        string? clientId,
        string? code,
        string? scope,
        string? nickname,
        CancellationToken ct)
    {
        if (!_weChatOptions.Enabled)
            return BadRequest(new { error = "unsupported_grant_type", error_description = "WeChat mini program login is disabled." });

        if (string.IsNullOrWhiteSpace(clientId)
            || !string.Equals(clientId, _weChatOptions.ClientId, StringComparison.Ordinal))
        {
            return InvalidClient();
        }

        var clientAuthentication = await clientService.AuthenticateClientAsync(
            new ClientAuthenticationInput(clientId, null, null, null),
            requireClientAuthentication: false,
            ct);
        if (!clientAuthentication.IsSuccess || clientAuthentication.Client is null)
            return InvalidClient();

        if (!string.Equals(clientAuthentication.Client.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
            return InvalidClient();

        if (!securityPolicyService.CheckClient(clientId).IsSuccess)
            return BadRequest(new { error = "unauthorized_client", error_description = "Client is blocked by security policy." });

        if (!clientAuthentication.Client.IsGrantTypeAllowed(WeChatMiniProgramGrantTypes.GrantType))
        {
            return BadRequest(new
            {
                error = "unauthorized_client",
                error_description = "Client is not allowed to use the WeChat mini program grant type.",
            });
        }

        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "invalid_request", error_description = "code is required." });

        if (string.IsNullOrWhiteSpace(scope))
            return BadRequest(new { error = "invalid_request", error_description = "scope is required." });

        var scopeValidation = await clientService.ValidateScopesAsync(clientId, scope, allowIdentityScopes: true, ct);
        if (!scopeValidation.IsSuccess)
        {
            return BadRequest(new
            {
                error = scopeValidation.ErrorCode ?? "invalid_scope",
                error_description = scopeValidation.Error,
            });
        }

        if (scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("offline_access", StringComparer.Ordinal)
            && !clientAuthentication.Client.IsGrantTypeAllowed("refresh_token"))
        {
            return BadRequest(new
            {
                error = "unauthorized_client",
                error_description = "Client is not allowed to use refresh_token.",
            });
        }

        if (nickname is not null)
        {
            nickname = nickname.Trim();
            if (nickname.Length < 1 || nickname.EnumerateRunes().Count() > 100)
                return BadRequest(new { error = "invalid_request", error_description = "Nickname must contain between 1 and 100 characters." });

            if (!scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("profile", StringComparer.Ordinal))
                return BadRequest(new { error = "invalid_scope", error_description = "The profile scope is required to set a nickname." });
        }

        WeChatMiniProgramLoginResult login;
        try
        {
            login = await weChatMiniProgramLoginService.AuthenticateAsync(code, ct);
        }
        catch (WeChatMiniProgramLoginException exception)
        {
            return StatusCode(exception.ErrorCode == "temporarily_unavailable" ? 503 : 400,
                new { error = exception.ErrorCode, error_description = exception.Message });
        }

        var userPolicy = securityPolicyService.CheckUser(login.User);
        if (!userPolicy.IsSuccess || !login.User.IsActive)
            return BadRequest(new { error = "invalid_grant", error_description = "The user account is unavailable." });

        if (nickname is not null && login.User.IsSystemAccount)
            return BadRequest(new { error = "invalid_grant", error_description = "System account profiles cannot be modified." });

        if (nickname is not null && !string.Equals(login.User.Nickname, nickname, StringComparison.Ordinal))
        {
            login.User.UpdateNickname(nickname);
            await userRepository.UpdateAsync(login.User, ct);
        }

        var accessTokenResult = await tokenService.IssueAccessTokenWithMetadataAsync(
            clientId,
            scopeValidation.NormalizedScope!,
            null,
            login.User.Id,
            null,
            ct);
        var refreshToken = ShouldIssueRefreshToken(scopeValidation.NormalizedScope!)
            ? await tokenService.IssueRefreshTokenAsync(clientId, login.User.Id, scopeValidation.NormalizedScope!, ct)
            : null;
        var includeIdToken = scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("openid", StringComparer.Ordinal);
        string? idToken = null;
        if (includeIdToken)
        {
            idToken = await tokenService.IssueIdTokenAsync(
                clientId,
                login.User.Id,
                null,
                accessTokenResult.AccessToken,
                authenticatedAt: login.AuthenticatedAt,
                acr: "urn:nexusauth:acr:external",
                amr: "wechat",
                ct: ct);
        }

        return Ok(new
        {
            access_token = accessTokenResult.AccessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            scope = scopeValidation.NormalizedScope,
            id_token = idToken,
            expires_in = GetAccessTokenLifetimeSeconds(),
            user = new
            {
                sub = login.User.Id.ToString(),
                name = login.User.Nickname,
            },
        });
    }

    private async Task<IActionResult> HandleAuthorizationCodeAsync(
        ClientAuthenticationInput authentication,
        string? code,
        string? redirectUri,
        string? codeVerifier,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient();

        var clientAuthentication = await clientService.AuthenticateClientAsync(authentication, requireClientAuthentication: true, ct);
        if (!clientAuthentication.IsSuccess)
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "invalid_request", error_description = "code is required." });

        if (string.IsNullOrWhiteSpace(redirectUri))
            return BadRequest(new { error = "invalid_request", error_description = "redirect_uri is required." });

        var result = await authorizationService.ValidateAndConsumeCodeAsync(code, authentication.ClientId, redirectUri, codeVerifier, ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = "invalid_grant", error_description = result.Error });

        if (!await IsActiveUserAsync(result.UserId, ct))
            return BadRequest(new { error = "invalid_grant", error_description = "The user account is no longer active." });

        var accessTokenResult = await tokenService.IssueAccessTokenWithMetadataAsync(result.ClientId, result.Scope, null, result.UserId, result.ClaimsJson, ct);
        var refreshToken = ShouldIssueRefreshToken(result.Scope)
            ? await tokenService.IssueRefreshTokenAsync(result.ClientId, result.UserId, result.Scope, ct)
            : null;
        var includeIdToken = result.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal);
        string? idToken = null;
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

        return Ok(new
        {
            access_token = accessTokenResult.AccessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            scope = result.Scope,
            id_token = idToken,
            expires_in = GetAccessTokenLifetimeSeconds(),
        });
    }

    private async Task<IActionResult> HandleClientCredentialsAsync(
        ClientAuthenticationInput authentication,
        string? scope,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient();

        var clientAuthentication = await clientService.AuthenticateClientAsync(authentication, requireClientAuthentication: true, ct);
        if (!clientAuthentication.IsSuccess || clientAuthentication.Client is null)
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(scope))
            return BadRequest(new { error = "invalid_request", error_description = "scope is required." });

        var policy = securityPolicyService.CheckClient(clientAuthentication.Client.ClientId);
        if (!policy.IsSuccess)
            return BadRequest(new { error = "access_denied", error_description = policy.Error });

        if (!clientAuthentication.Client.IsGrantTypeAllowed("client_credentials"))
        {
            return BadRequest(new
            {
                error = "unauthorized_client",
                error_description = "Client is not allowed to use client_credentials grant type.",
            });
        }

        var scopeValidation = await clientService.ValidateScopesAsync(
            clientAuthentication.Client.ClientId,
            scope,
            allowIdentityScopes: false,
            ct);

        if (!scopeValidation.IsSuccess)
            return BadRequest(new { error = scopeValidation.ErrorCode ?? "invalid_scope", error_description = scopeValidation.Error });

        var accessToken = await tokenService.IssueAccessTokenAsync(
            clientAuthentication.Client.ClientId,
            scopeValidation.NormalizedScope!,
            null,
            null,
            null,
            ct);

        return Ok(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            scope = scopeValidation.NormalizedScope,
            expires_in = GetAccessTokenLifetimeSeconds(),
        });
    }

    private async Task<IActionResult> HandleRefreshTokenAsync(
        ClientAuthenticationInput authentication,
        string? refreshToken,
        CancellationToken ct)
    {
        // 中文注释：refresh_token 分支会先校验客户端身份，再调用 TokenService 做旧 refresh token 吊销和新 token 轮换。
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient();

        var isConfiguredMiniProgram = _weChatOptions.Enabled
            && string.Equals(authentication.ClientId, _weChatOptions.ClientId, StringComparison.Ordinal);
        var clientAuthentication = await clientService.AuthenticateClientAsync(authentication,
            requireClientAuthentication: !isConfiguredMiniProgram, ct);
        if (!clientAuthentication.IsSuccess)
            return InvalidClient();

        if (isConfiguredMiniProgram && (clientAuthentication.Client?.TokenEndpointAuthMethod != "none"
            || authentication.ClientSecret is not null || authentication.ClientAssertion is not null
            || authentication.ClientAssertionType is not null || !string.IsNullOrWhiteSpace(Request.Headers.Authorization)))
            return InvalidClient();

        if (clientAuthentication.Client?.IsGrantTypeAllowed("refresh_token") != true
            || (isConfiguredMiniProgram && !clientAuthentication.Client.IsGrantTypeAllowed(WeChatMiniProgramGrantTypes.GrantType))
            || !securityPolicyService.CheckClient(authentication.ClientId!).IsSuccess)
            return BadRequest(new { error = "unauthorized_client", error_description = "Client is not allowed to refresh tokens." });

        if (string.IsNullOrWhiteSpace(refreshToken))
            return BadRequest(new { error = "invalid_request", error_description = "refresh_token is required." });

        var result = await tokenService.RefreshAsync(refreshToken, authentication.ClientId!, ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = "invalid_grant", error_description = result.Error });

        return Ok(new
        {
            access_token = result.AccessToken,
            token_type = "Bearer",
            refresh_token = result.RefreshToken,
            expires_in = GetAccessTokenLifetimeSeconds(),
        });
    }

    private async Task<IActionResult> HandleDeviceCodeAsync(
        ClientAuthenticationInput authentication,
        string? deviceCode,
        CancellationToken ct)
    {
        // 中文注释：device_code 分支本身只负责“兑换”，真正的 device_code 状态机在 DeviceAuthorizationService 中维护。
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(deviceCode))
            return BadRequest(new { error = "invalid_request", error_description = "device_code is required." });

        var result = await deviceAuthorizationService.PollAsync(authentication, deviceCode, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "invalid_client")
                return InvalidClient();

            if (result.ErrorCode == "invalid_request")
                return BadRequest(new { error = "invalid_request", error_description = result.Error });

            if (result.ErrorCode is "unauthorized_client" or "access_denied")
                return BadRequest(new { error = result.ErrorCode, error_description = result.Error });

            if (result.ErrorCode is "authorization_pending" or "slow_down" or "expired_token")
                return BadRequest(new { error = result.ErrorCode, error_description = result.Error, interval = result.Interval > 0 ? (int?)result.Interval : null });

            return BadRequest(new { error = "invalid_grant", error_description = result.Error });
        }

        if (!await IsActiveUserAsync(result.UserId, ct))
            return BadRequest(new { error = "invalid_grant", error_description = "The user account is no longer active." });

        var accessTokenResult = await tokenService.IssueAccessTokenWithMetadataAsync(result.ClientId!, result.Scope!, null, result.UserId, null, ct);
        var refreshToken = ShouldIssueRefreshToken(result.Scope!)
            ? await tokenService.IssueRefreshTokenAsync(result.ClientId!, result.UserId, result.Scope!, ct)
            : null;
        var includeIdToken = result.Scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal);
        string? idToken = null;
        if (includeIdToken)
            idToken = await tokenService.IssueIdTokenAsync(result.ClientId!, result.UserId, null, accessTokenResult.AccessToken, ct: ct);

        return Ok(new
        {
            access_token = accessTokenResult.AccessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            scope = result.Scope,
            id_token = idToken,
            expires_in = GetAccessTokenLifetimeSeconds(),
        });
    }

    private string GetEndpointAudience()
    {
        var request = HttpContext.Request;
        var host = request.Host.HasValue ? request.Host.Value : null;
        if (!string.IsNullOrWhiteSpace(host))
            return $"{request.Scheme}://{host}{request.PathBase}{request.Path}";

        return _jwtOptions.Issuer.TrimEnd('/') + "/connect/token";
    }

    private string GetIssuer()
    {
        return _jwtOptions.Issuer.TrimEnd('/');
    }

    private int GetAccessTokenLifetimeSeconds()
    {
        return checked(_jwtOptions.AccessTokenLifetimeMinutes * 60);
    }

    private async Task<bool> IsActiveUserAsync(Guid userId, CancellationToken ct)
    {
        return (await userService.FindByIdAsync(userId, ct))?.IsActive == true;
    }

    private IActionResult InvalidClient()
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"NexusAuth\"";
        return Unauthorized(new { error = "invalid_client" });
    }

    private static bool ShouldIssueRefreshToken(string scope)
    {
        // 中文注释：refresh token 只在请求了 offline_access 时才签发，
        // 这样 discovery 文档里的 OIDC 能力声明和真实行为保持一致。
        // 主要调用方：authorization_code 与 device_code 的换 token 流程。
        return scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("offline_access", StringComparer.Ordinal);
    }
}
