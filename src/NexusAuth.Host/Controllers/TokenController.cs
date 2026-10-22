using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services;
using NexusAuth.Application.Services.ExternalIdentity;

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
    IOptions<WeChatMiniProgramOptions> weChatOptions) : ControllerBase
{
    private readonly IAuthorizationService _authorizationService = authorizationService;
    private readonly ITokenService _tokenService = tokenService;
    private readonly IDeviceAuthorizationService _deviceAuthorizationService = deviceAuthorizationService;
    private readonly IClientService _clientService = clientService;
    private readonly IUserService _userService = userService;
    private readonly ISecurityPolicyService _securityPolicyService = securityPolicyService;
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;
    private readonly IWeChatMiniProgramLoginService _weChatMiniProgramLoginService = weChatMiniProgramLoginService;
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

            return await HandleWeChatMiniProgramAsync(clientId, code, scope, ct);
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
            GetTokenEndpointUrl(),
            GetAcceptedAssertionAudiences());

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
        CancellationToken ct)
    {
        if (!_weChatOptions.Enabled)
            return BadRequest(new { error = "unsupported_grant_type", error_description = "WeChat mini program login is disabled." });

        if (string.IsNullOrWhiteSpace(clientId)
            || !string.Equals(clientId, _weChatOptions.ClientId, StringComparison.Ordinal))
        {
            return InvalidClient();
        }

        var clientAuthentication = await _clientService.AuthenticateClientAsync(
            new ClientAuthenticationInput(clientId, null, null, null),
            requireClientAuthentication: false,
            ct);
        if (!clientAuthentication.IsSuccess || clientAuthentication.Client is null)
            return InvalidClient();

        if (!string.Equals(clientAuthentication.Client.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
            return InvalidClient();

        if (!_securityPolicyService.CheckClient(clientId).IsSuccess)
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

        var scopeValidation = await _clientService.ValidateScopesAsync(clientId, scope, allowIdentityScopes: true, ct);
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

        WeChatMiniProgramLoginResult login;
        try
        {
            login = await _weChatMiniProgramLoginService.AuthenticateAsync(code, ct);
        }
        catch (WeChatMiniProgramLoginException exception)
        {
            return StatusCode(exception.ErrorCode == "temporarily_unavailable" ? 503 : 400,
                new { error = exception.ErrorCode, error_description = exception.Message });
        }

        var userPolicy = _securityPolicyService.CheckUser(login.User);
        if (!userPolicy.IsSuccess || !login.User.IsActive)
            return BadRequest(new { error = "invalid_grant", error_description = "The user account is unavailable." });

        TokenIssueResult accessTokenResult;
        string? refreshToken;
        string? idToken = null;
        try
        {
            accessTokenResult = await _tokenService.IssueAccessTokenWithMetadataAsync(
                clientId,
                scopeValidation.NormalizedScope!,
                null,
                login.User.Id,
                null,
                ct);
            refreshToken = ShouldIssueRefreshToken(scopeValidation.NormalizedScope!)
                ? await _tokenService.IssueRefreshTokenAsync(clientId, login.User.Id, scopeValidation.NormalizedScope!, ct)
                : null;
            var includeIdToken = scopeValidation.NormalizedScope!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("openid", StringComparer.Ordinal);
            if (includeIdToken)
            {
                idToken = await _tokenService.IssueIdTokenAsync(
                    clientId,
                    login.User.Id,
                    null,
                    accessTokenResult.AccessToken,
                    authenticatedAt: login.AuthenticatedAt,
                    acr: "urn:nexusauth:acr:external",
                    amr: "wechat",
                    ct: ct);
            }
        }
        catch (InvalidOperationException)
        {
            return TokenIssuanceFailed();
        }

        return Ok(new
        {
            access_token = accessTokenResult.AccessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            scope = scopeValidation.NormalizedScope,
            id_token = idToken,
            expires_in = GetAccessTokenLifetimeSeconds(),
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

        var clientAuthentication = await _clientService.AuthenticateClientAsync(authentication, requireClientAuthentication: true, ct);
        if (!clientAuthentication.IsSuccess)
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "invalid_request", error_description = "code is required." });

        if (string.IsNullOrWhiteSpace(redirectUri))
            return BadRequest(new { error = "invalid_request", error_description = "redirect_uri is required." });

        var result = await _authorizationService.ValidateAndConsumeCodeAsync(code, authentication.ClientId, redirectUri, codeVerifier, ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = "invalid_grant", error_description = result.Error });

        if (!await IsActiveUserAsync(result.UserId, ct))
            return BadRequest(new { error = "invalid_grant", error_description = "The user account is no longer active." });

        TokenIssueResult accessTokenResult;
        string? refreshToken;
        string? idToken = null;
        try
        {
            accessTokenResult = await _tokenService.IssueAccessTokenWithMetadataAsync(result.ClientId, result.Scope, null, result.UserId, result.ClaimsJson, ct);
            refreshToken = ShouldIssueRefreshToken(result.Scope)
                ? await _tokenService.IssueRefreshTokenAsync(result.ClientId, result.UserId, result.Scope, ct)
                : null;
            var includeIdToken = result.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal);
            if (includeIdToken)
            {
                // 中文注释：授权码里已经保存了 OIDC 上下文，这里在换 token 时继续带到 id_token 里。
                idToken = await _tokenService.IssueIdTokenAsync(
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

        var clientAuthentication = await _clientService.AuthenticateClientAsync(authentication, requireClientAuthentication: true, ct);
        if (!clientAuthentication.IsSuccess || clientAuthentication.Client is null)
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(scope))
            return BadRequest(new { error = "invalid_request", error_description = "scope is required." });

        var policy = _securityPolicyService.CheckClient(clientAuthentication.Client.ClientId);
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

        var scopeValidation = await _clientService.ValidateScopesAsync(
            clientAuthentication.Client.ClientId,
            scope,
            allowIdentityScopes: false,
            ct);

        if (!scopeValidation.IsSuccess)
            return BadRequest(new { error = scopeValidation.ErrorCode ?? "invalid_scope", error_description = scopeValidation.Error });

        string accessToken;
        try
        {
            accessToken = await _tokenService.IssueAccessTokenAsync(
                clientAuthentication.Client.ClientId,
                scopeValidation.NormalizedScope!,
                null,
                null,
                null,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid_scope", error_description = ex.Message });
        }

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
        var clientAuthentication = await _clientService.AuthenticateClientAsync(authentication,
            requireClientAuthentication: !isConfiguredMiniProgram, ct);
        if (!clientAuthentication.IsSuccess)
            return InvalidClient();

        if (isConfiguredMiniProgram && (clientAuthentication.Client?.TokenEndpointAuthMethod != "none"
            || authentication.ClientSecret is not null || authentication.ClientAssertion is not null
            || authentication.ClientAssertionType is not null || !string.IsNullOrWhiteSpace(Request.Headers.Authorization)))
            return InvalidClient();

        if (clientAuthentication.Client?.IsGrantTypeAllowed("refresh_token") != true
            || (isConfiguredMiniProgram && !clientAuthentication.Client.IsGrantTypeAllowed(WeChatMiniProgramGrantTypes.GrantType))
            || !_securityPolicyService.CheckClient(authentication.ClientId!).IsSuccess)
            return BadRequest(new { error = "unauthorized_client", error_description = "Client is not allowed to refresh tokens." });

        if (string.IsNullOrWhiteSpace(refreshToken))
            return BadRequest(new { error = "invalid_request", error_description = "refresh_token is required." });

        var result = await _tokenService.RefreshAsync(refreshToken, authentication.ClientId!, ct);

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

        var result = await _deviceAuthorizationService.PollAsync(authentication, deviceCode, ct);
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

        TokenIssueResult accessTokenResult;
        string? refreshToken;
        string? idToken = null;
        try
        {
            accessTokenResult = await _tokenService.IssueAccessTokenWithMetadataAsync(result.ClientId!, result.Scope!, null, result.UserId, null, ct);
            refreshToken = ShouldIssueRefreshToken(result.Scope!)
                ? await _tokenService.IssueRefreshTokenAsync(result.ClientId!, result.UserId, result.Scope!, ct)
                : null;
            var includeIdToken = result.Scope!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal);
            if (includeIdToken)
                idToken = await _tokenService.IssueIdTokenAsync(result.ClientId!, result.UserId, null, accessTokenResult.AccessToken, ct: ct);
        }
        catch (InvalidOperationException)
        {
            return TokenIssuanceFailed();
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

    private string GetTokenEndpointUrl()
    {
        return GetIssuer() + "/connect/token";
    }

    /// <summary>
    /// 中文注释：client assertion 的 aud 只接受由配置的 Jwt:Issuer 推导出的值（RFC 7523 §3：
    /// issuer 本身或 token endpoint URL），不再信任可被伪造的 Host 头，也兼容 TLS 反向代理场景。
    /// </summary>
    private IReadOnlyList<string> GetAcceptedAssertionAudiences()
    {
        return [GetTokenEndpointUrl(), GetIssuer()];
    }

    private IActionResult TokenIssuanceFailed()
    {
        return BadRequest(new { error = "invalid_grant", error_description = "Unable to issue tokens for this grant." });
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
        // 中文注释：换 token 时同时校验账号状态与当前安全策略（黑/白名单）。
        var user = await _userService.FindByIdAsync(userId, ct);
        return user is { IsActive: true } && _securityPolicyService.CheckUser(user).IsSuccess;
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
