using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Services;
using NexusAuth.Application.Services.ExternalIdentity;

namespace NexusAuth.Host.Controllers;

[ApiController]
public class OpenIdController(
    ITokenSigningCredentialsProvider signingCredentialsProvider,
    IIdTokenHintValidator idTokenHintValidator,
    ITokenService tokenService,
    IUserService userService,
    IClientService clientService,
    IDeviceAuthorizationService deviceAuthorizationService,
    ISsoSessionService sessionService,
    IAntiforgery antiforgery,
    IOptions<JwtOptions> jwtOptions,
    IOptions<WeChatMiniProgramOptions>? weChatOptions = null) : ControllerBase
{
    private readonly ITokenSigningCredentialsProvider _signingCredentialsProvider = signingCredentialsProvider;
    private readonly IIdTokenHintValidator _idTokenHintValidator = idTokenHintValidator;
    private readonly ITokenService _tokenService = tokenService;
    private readonly IUserService _userService = userService;
    private readonly IClientService _clientService = clientService;
    private readonly IDeviceAuthorizationService _deviceAuthorizationService = deviceAuthorizationService;
    private readonly ISsoSessionService _sessionService = sessionService;
    private readonly IAntiforgery _antiforgery = antiforgery;
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;
    private static readonly HashSet<string> BaseUserInfoClaims =
    [
        "sub",
        "preferred_username",
        "name",
        "email",
        "phone_number",
        "email_verified",
        "phone_number_verified",
    ];

    /// <summary>
    /// OIDC Discovery 元数据端点。
    /// 主要调用方：
    /// - Demo.Bff / Demo.Bff.ClientSecret
    /// - Demo.ClientCredentials
    /// - Demo.DeviceCode
    /// - Demo.RefreshToken
    /// - Grafana / 其他外部 OIDC Client
    /// </summary>
    [HttpGet("/.well-known/openid-configuration")]
    public IActionResult Discovery()
    {
        var issuer = GetIssuer();
        return Ok(new
        {
            issuer,
            authorization_endpoint = $"{issuer}/connect/authorize",
            token_endpoint = $"{issuer}/connect/token",
            userinfo_endpoint = $"{issuer}/connect/userinfo",
            jwks_uri = $"{issuer}/.well-known/jwks.json",
            revocation_endpoint = $"{issuer}/connect/revocation",
            introspection_endpoint = $"{issuer}/connect/introspect",
            device_authorization_endpoint = $"{issuer}/connect/deviceauthorization",
            end_session_endpoint = $"{issuer}/connect/endsession",
            scopes_supported = new[] { "openid", "profile", "email", "phone", "address", "offline_access" },
            response_types_supported = new[] { "code" },
            response_modes_supported = new[] { "query", "form_post" },
            grant_types_supported = new[]
            {
                "authorization_code",
                "client_credentials",
                "refresh_token",
                "urn:ietf:params:oauth:grant-type:device_code",
            }.Concat(weChatOptions?.Value.Enabled == true ? [WeChatMiniProgramGrantTypes.GrantType] : Array.Empty<string>()),
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { _signingCredentialsProvider.Algorithm },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic", "client_secret_jwt", "private_key_jwt" }
                .Concat(weChatOptions?.Value.Enabled == true ? ["none"] : Array.Empty<string>()),
            token_endpoint_auth_signing_alg_values_supported = new[] { "HS256", "RS256" },
            claims_supported = new[]
            {
                "sub", "preferred_username", "name", "nickname", "email", "phone_number", "email_verified",
                "phone_number_verified", "gender", "ethnicity", "address", "nonce", "at_hash", "auth_time", "acr", "amr"
            },
            code_challenge_methods_supported = new[] { "S256" },
            claims_parameter_supported = true,
            request_parameter_supported = false,
            request_uri_parameter_supported = false,
            prompt_values_supported = new[] { "none", "login", "consent" },
        });
    }

    /// <summary>
    /// JWKS 公钥端点。
    /// </summary>
    [HttpGet("/.well-known/jwks.json")]
    public IActionResult Jwks()
    {
        return Ok(new { keys = _signingCredentialsProvider.GetJwks() });
    }

    /// <summary>
    /// OIDC UserInfo 端点。
    /// 主要流程：
    /// 1. 解析 Bearer access_token
    /// 2. 通过 TokenService 做 introspection 风格校验
    /// 3. 按 scope 和 claims 参数返回用户信息
    /// </summary>
    [HttpGet("/connect/userinfo")]
    public async Task<IActionResult> UserInfo(CancellationToken ct)
    {
        var authorization = Request.Headers.Authorization.ToString();
        var bearerToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..]
            : null;

        if (string.IsNullOrWhiteSpace(bearerToken))
            return Unauthorized(new { error = "invalid_token", error_description = "Bearer token is required." });

        var introspection = await _tokenService.IntrospectAsync(bearerToken, ct);
        if (!introspection.Active)
            return Unauthorized(new { error = "invalid_token", error_description = "Access token is invalid." });

        if (!string.Equals(introspection.TokenUse, "access_token", StringComparison.Ordinal))
            return Unauthorized(new { error = "invalid_token", error_description = "Only access tokens can call userinfo." });

        if (!Guid.TryParse(introspection.Subject, out var userId))
            return Unauthorized(new { error = "invalid_token", error_description = "Access token does not represent a user." });

        var user = await _userService.FindByIdAsync(userId, ct);
        if (user is null || !user.IsActive)
            return Unauthorized(new { error = "invalid_token", error_description = "User not found." });

        var requestedClaims = ResolveRequestedClaims(introspection.Scope, introspection.ClaimsJson);

        return Ok(BuildUserInfoPayload(user, requestedClaims));
    }

    /// <summary>
    /// Device Authorization 端点。
    /// 主要调用方：Demo.DeviceCode，以及任意符合 RFC 8628 的设备端客户端。
    /// </summary>
    [HttpPost("/connect/deviceauthorization")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> DeviceAuthorization(
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret,
        [FromForm(Name = "client_assertion_type")] string? clientAssertionType,
        [FromForm(Name = "client_assertion")] string? clientAssertion,
        [FromForm] string? scope,
        CancellationToken ct = default)
    {
        var resolvedClientAuthentication = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            Request.Headers.Authorization.ToString(),
            clientId,
            clientSecret,
            clientAssertionType,
            clientAssertion,
            GetTokenEndpointUrl(),
            GetAcceptedAssertionAudiences("/connect/deviceauthorization"));

        if (!resolvedClientAuthentication.IsSuccess || resolvedClientAuthentication.Authentication is null)
            return InvalidClient();

        var authentication = resolvedClientAuthentication.Authentication;
        if (string.IsNullOrWhiteSpace(authentication.ClientId))
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(scope))
            return BadRequest(new { error = "invalid_request", error_description = "scope is required." });

        var result = await _deviceAuthorizationService.StartAsync(authentication, scope, ct);
        if (!result.IsSuccess)
        {
            if (string.Equals(result.ErrorCode, "invalid_client", StringComparison.Ordinal))
                return InvalidClient();

            return BadRequest(new { error = result.ErrorCode, error_description = result.Error });
        }

        var issuer = GetIssuer();
        var verificationUri = $"{issuer}{result.VerificationUri}";
        var verificationUriComplete = $"{issuer}{result.VerificationUriComplete}";

        return Ok(new
        {
            device_code = result.DeviceCode,
            user_code = result.UserCode,
            verification_uri = verificationUri,
            verification_uri_complete = verificationUriComplete,
            expires_in = result.ExpiresIn,
            interval = result.Interval,
        });
    }

    /// <summary>
    /// OAuth2 Introspection 端点。
    /// </summary>
    [HttpPost("/connect/introspect")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Introspect(
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret,
        [FromForm(Name = "client_assertion_type")] string? clientAssertionType,
        [FromForm(Name = "client_assertion")] string? clientAssertion,
        [FromForm] string? token,
        CancellationToken ct = default)
    {
        var resolvedClientAuthentication = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            Request.Headers.Authorization.ToString(),
            clientId,
            clientSecret,
            clientAssertionType,
            clientAssertion,
            GetTokenEndpointUrl(),
            GetAcceptedAssertionAudiences("/connect/introspect"));

        if (!resolvedClientAuthentication.IsSuccess || resolvedClientAuthentication.Authentication is null)
            return InvalidClient();

        var authenticationInput = resolvedClientAuthentication.Authentication;
        if (string.IsNullOrWhiteSpace(authenticationInput.ClientId))
            return InvalidClient();

        var authentication = await _clientService.AuthenticateClientAsync(authenticationInput, requireClientAuthentication: true, ct);
        if (!authentication.IsSuccess)
            return InvalidClient();

        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { error = "invalid_request", error_description = "token is required." });

        var result = await _tokenService.IntrospectAsync(token, authenticationInput.ClientId, ct);
        return Ok(new
        {
            active = result.Active,
            sub = result.Subject,
            client_id = result.ClientId,
            scope = result.Scope,
            exp = result.Exp,
            iss = result.Issuer,
            token_use = result.TokenUse,
        });
    }

    /// <summary>
    /// OAuth2 Revocation 端点。
    /// 主要流程：
    /// 1. 校验客户端身份
    /// 2. 对 access_token / refresh_token 做客户端边界检查
    /// 3. 吊销 token 或加入黑名单
    /// </summary>
    [HttpPost("/connect/revocation")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Revoke(
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret,
        [FromForm(Name = "client_assertion_type")] string? clientAssertionType,
        [FromForm(Name = "client_assertion")] string? clientAssertion,
        [FromForm] string? token,
        [FromForm(Name = "token_type_hint")] string? tokenTypeHint,
        CancellationToken ct = default)
    {
        var resolvedClientAuthentication = OAuthClientAuthenticationParser.ResolveClientAuthentication(
            Request.Headers.Authorization.ToString(),
            clientId,
            clientSecret,
            clientAssertionType,
            clientAssertion,
            GetTokenEndpointUrl(),
            GetAcceptedAssertionAudiences("/connect/revocation"));

        if (!resolvedClientAuthentication.IsSuccess || resolvedClientAuthentication.Authentication is null)
            return InvalidClient();

        var authenticationInput = resolvedClientAuthentication.Authentication;
        if (string.IsNullOrWhiteSpace(authenticationInput.ClientId))
            return InvalidClient();

        if (!await AuthenticateRevocationClientAsync(authenticationInput, ct))
            return InvalidClient();

        if (!string.IsNullOrWhiteSpace(token))
        {
            // 中文注释：token_type_hint 只是查找提示；按提示找不到时必须继续尝试其他 token 类型（RFC 7009 §2.1）。
            if (string.Equals(tokenTypeHint, "refresh_token", StringComparison.OrdinalIgnoreCase))
            {
                if (!await RevokeRefreshTokenForClientAsync(token, authenticationInput.ClientId, ct))
                    await _tokenService.RevokeAccessTokenAsync(token, authenticationInput.ClientId, ct);
            }
            else
            {
                await _tokenService.RevokeAccessTokenAsync(token, authenticationInput.ClientId, ct);
                await RevokeRefreshTokenForClientAsync(token, authenticationInput.ClientId, ct);
            }
        }

        return Ok();
    }

    /// <summary>
    /// 中文注释：机密客户端必须完成客户端认证；token_endpoint_auth_method=none 的公共客户端
    /// （例如微信小程序）只能凭 client_id 吊销属于自己的 token（RFC 7009 §2.1），且不得携带任何凭据。
    /// </summary>
    private async Task<bool> AuthenticateRevocationClientAsync(ClientAuthenticationInput input, CancellationToken ct)
    {
        var lookup = await _clientService.AuthenticateClientAsync(input, requireClientAuthentication: false, ct);
        if (!lookup.IsSuccess || lookup.Client is null)
            return false;

        if (string.Equals(lookup.Client.TokenEndpointAuthMethod, Domain.AggregateRoots.OAuthClients.OAuthClient.TokenEndpointAuthMethodNone, StringComparison.Ordinal))
        {
            return input.ClientSecret is null
                && input.ClientAssertion is null
                && input.ClientAssertionType is null
                && string.IsNullOrWhiteSpace(Request.Headers.Authorization);
        }

        var authentication = await _clientService.AuthenticateClientAsync(input, requireClientAuthentication: true, ct);
        return authentication.IsSuccess;
    }

    /// <summary>
    /// OIDC RP-Initiated Logout 端点。
    /// </summary>
    [HttpGet("/connect/endsession")]
    public async Task<IActionResult> EndSession(
        [FromQuery(Name = "id_token_hint")] string? idTokenHint = null,
        [FromQuery(Name = "post_logout_redirect_uri")] string? postLogoutRedirectUri = null,
        [FromQuery] string? state = null,
        CancellationToken ct = default)
    {
        var validationError = await ValidateEndSessionRequestAsync(idTokenHint, postLogoutRedirectUri, ct);
        if (validationError is not null)
            return validationError;

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        return Content(
            BuildEndSessionConfirmationPage(
                tokens.FormFieldName,
                tokens.RequestToken!,
                idTokenHint,
                postLogoutRedirectUri,
                state),
            "text/html; charset=utf-8");
    }

    [HttpPost("/connect/endsession")]
    [Consumes("application/x-www-form-urlencoded")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmEndSession(
        [FromForm(Name = "id_token_hint")] string? idTokenHint = null,
        [FromForm(Name = "post_logout_redirect_uri")] string? postLogoutRedirectUri = null,
        [FromForm] string? state = null,
        CancellationToken ct = default)
    {
        var validationError = await ValidateEndSessionRequestAsync(idTokenHint, postLogoutRedirectUri, ct);
        if (validationError is not null)
            return validationError;

        var subject = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var sid = User.FindFirst("sid")?.Value;
        if (Guid.TryParse(subject, out var currentUserId) && Guid.TryParse(sid, out _))
        {
            await _tokenService.RevokeAllUserTokensAsync(currentUserId, ct);
            await _sessionService.RevokeAllForUserAsync(currentUserId, ct);
        }

        await HttpContext.SignOutAsync(AppWebModule.AuthenticationScheme);

        if (!string.IsNullOrWhiteSpace(postLogoutRedirectUri)
            && Uri.TryCreate(postLogoutRedirectUri, UriKind.Absolute, out _))
        {
            var redirectUri = postLogoutRedirectUri;
            if (!string.IsNullOrWhiteSpace(state))
                redirectUri = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUri, "state", state);

            return Redirect(redirectUri);
        }

        return Redirect("/");
    }

    private async Task<IActionResult?> ValidateEndSessionRequestAsync(
        string? idTokenHint,
        string? postLogoutRedirectUri,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idTokenHint))
        {
            return string.IsNullOrWhiteSpace(postLogoutRedirectUri)
                ? null
                : BadRequest(new { error = "invalid_request", error_description = "id_token_hint is required when post_logout_redirect_uri is provided." });
        }

        var validation = _idTokenHintValidator.Validate(idTokenHint);
        if (!validation.IsValid)
            return BadRequest(new { error = "invalid_request", error_description = "id_token_hint is invalid." });

        var currentSubject = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(currentSubject)
            && !string.IsNullOrWhiteSpace(validation.Subject)
            && !string.Equals(currentSubject, validation.Subject, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "invalid_request", error_description = "id_token_hint does not match the current session." });
        }

        var clientValidation = await _clientService.AuthenticateClientForPostLogoutAsync(
            validation.ClientId!,
            postLogoutRedirectUri,
            ct);
        return clientValidation.IsSuccess
            ? null
            : BadRequest(new { error = clientValidation.ErrorCode ?? "invalid_request", error_description = clientValidation.Error });
    }

    private static string BuildEndSessionConfirmationPage(
        string antiforgeryFieldName,
        string antiforgeryToken,
        string? idTokenHint,
        string? postLogoutRedirectUri,
        string? state)
    {
        static string Encode(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

        return $"""
            <!doctype html>
            <html lang="zh-CN">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>退出登录 - NexusAuth</title></head>
            <body style="margin:0;font-family:system-ui,sans-serif;background:#f4f6f8;color:#18212f">
              <main style="max-width:480px;margin:12vh auto;padding:28px;border:1px solid #d8dee8;border-radius:8px;background:#fff">
                <h1 style="margin:0 0 12px;font-size:24px">退出 NexusAuth？</h1>
                <p style="margin:0 0 24px;color:#526071;line-height:1.6">确认后将退出该账号在所有设备上的单点登录会话，并撤销所有应用的刷新令牌。部分应用已签发的访问令牌可能在到期前仍有效。</p>
                <form method="post" action="/connect/endsession">
                  <input type="hidden" name="{Encode(antiforgeryFieldName)}" value="{Encode(antiforgeryToken)}">
                  <input type="hidden" name="id_token_hint" value="{Encode(idTokenHint)}">
                  <input type="hidden" name="post_logout_redirect_uri" value="{Encode(postLogoutRedirectUri)}">
                  <input type="hidden" name="state" value="{Encode(state)}">
                  <button type="submit" style="border:0;border-radius:6px;padding:11px 18px;background:#1769e0;color:#fff;font-weight:600;cursor:pointer">确认退出</button>
                  <a href="/" style="margin-left:12px;color:#42526a;text-decoration:none">取消</a>
                </form>
              </main>
            </body>
            </html>
            """;
    }

    private string GetIssuer()
    {
        // 统一使用配置中的 Issuer，确保 discovery 的 issuer 与 token 的 iss 完全一致。
        return _jwtOptions.Issuer.TrimEnd('/');
    }

    private IActionResult InvalidClient()
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"NexusAuth\"";
        return Unauthorized(new { error = "invalid_client" });
    }

    private string GetTokenEndpointUrl()
    {
        return GetIssuer() + "/connect/token";
    }

    /// <summary>
    /// 中文注释：client assertion 的 aud 只接受由配置的 Jwt:Issuer 推导出的值：issuer、token endpoint，
    /// 以及当前端点在 issuer 下的 URL（兼容旧客户端）。不再使用可伪造的 Host 头和反向代理后的 http scheme。
    /// </summary>
    private IReadOnlyList<string> GetAcceptedAssertionAudiences(string endpointPath)
    {
        var issuer = GetIssuer();
        return [.. new[] { GetTokenEndpointUrl(), issuer, issuer + endpointPath }.Distinct(StringComparer.Ordinal)];
    }

    private static OidcRequestedClaims ResolveRequestedClaims(string? scope, string? claimsJson)
    {
        // 中文注释：先按已授予的 scope 得到基础 claim，再合并 claims 参数显式请求的 userinfo claim；
        // 两者都必须被已授予的 scope 覆盖（OIDC Core §5.4 / §5.5），claims 参数不能绕过 scope。
        var requestedClaims = OidcRequestedClaims.Parse(claimsJson);
        var grantedScopes = OidcClaimEmissionPolicy.ParseScopes(scope);
        var userInfoClaims = BaseUserInfoClaims
            .Where(claim => OidcClaimEmissionPolicy.IsClaimAllowedByScopes(claim, grantedScopes))
            .ToHashSet(StringComparer.Ordinal);

        if (requestedClaims.HasExplicitRequests)
        {
            userInfoClaims.UnionWith(requestedClaims.UserInfoClaims
                .Where(claim => OidcClaimEmissionPolicy.IsClaimAllowedByScopes(claim, grantedScopes)));
        }

        return OidcRequestedClaims.Create(userInfoClaims: userInfoClaims);
    }

    private static object BuildUserInfoPayload(Domain.AggregateRoots.Users.User user, OidcRequestedClaims requestedClaims)
    {
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = user.Id.ToString(),
        };

        if (requestedClaims.RequestsUserInfoClaim("preferred_username"))
            payload["preferred_username"] = user.Username;

        if (requestedClaims.RequestsUserInfoClaim("name"))
            payload["name"] = user.Nickname;

        if (requestedClaims.RequestsUserInfoClaim("nickname"))
            payload["nickname"] = user.Nickname;

        if (OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "email", user.Email))
            payload["email"] = user.Email;

        if (requestedClaims.RequestsUserInfoClaim("email_verified"))
            payload["email_verified"] = false;

        if (OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "phone_number", user.PhoneNumber))
            payload["phone_number"] = user.PhoneNumber;

        if (requestedClaims.RequestsUserInfoClaim("phone_number_verified"))
            payload["phone_number_verified"] = false;

        if (OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "gender", user.Gender.ToString()))
            payload["gender"] = user.Gender.ToString();

        if (OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "ethnicity", user.Ethnicity))
            payload["ethnicity"] = user.Ethnicity;

        if (requestedClaims.RequestsUserInfoClaim("address"))
        {
            payload["address"] = new Dictionary<string, object?>
            {
                ["formatted"] = null,
            };
        }

        return payload;
    }

    private async Task<bool> RevokeRefreshTokenForClientAsync(string token, string clientId, CancellationToken ct)
    {
        var isOwnedByClient = await _tokenService.IsRefreshTokenOwnedByClientAsync(token, clientId, ct);
        if (!isOwnedByClient)
            return false;

        await _tokenService.RevokeRefreshTokenAsync(token, clientId, ct);
        return true;
    }
}
