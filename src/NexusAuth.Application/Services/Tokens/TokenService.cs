using Luck.Logging.Serilog;
using System.Globalization;
using NexusAuth.Application.Services.OIDC;
using NexusAuth.Application.Services.Security;

namespace NexusAuth.Application.Services.Tokens;

public class TokenService(
    IRefreshTokenRepository refreshTokenRepository,
    ITokenBlacklistRepository tokenBlacklistRepository,
    IUserRepository userRepository,
    IApiResourceRepository apiResourceRepository,
    IOptions<JwtOptions> jwtOptions,
    ITokenSigningCredentialsProvider signingCredentialsProvider,
    ILogger<TokenService> logger,
    ISecurityPolicyService? securityPolicyService = null) : ITokenService
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    /// <summary>
    /// </summary>
    public async Task<string> IssueAccessTokenAsync(
        string clientId,
        string scope,
        string? audience = null,
        Guid? userId = null,
        string? claimsJson = null,
        CancellationToken ct = default)
    {
        // 中文注释：直接 await，保证异常以原始类型抛出（ContinueWith 会包装成 AggregateException）。
        var result = await IssueAccessTokenWithMetadataAsync(clientId, scope, audience, userId, claimsJson, ct);
        return result.AccessToken;
    }

    /// <summary>
    /// </summary>
    public async Task<TokenIssueResult> IssueAccessTokenWithMetadataAsync(
        string clientId,
        string scope,
        string? audience = null,
        Guid? userId = null,
        string? claimsJson = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var jti = Guid.NewGuid().ToString();
        var resolvedAudience = await ResolveAudienceAsync(scope, audience, ct);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId?.ToString() ?? clientId),
            new("client_id", clientId),
            new("scope", scope),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };

        if (userId.HasValue)
        {
            var user = await userRepository.FindByIdAsync(userId.Value, ct);
            if (user is not { IsActive: true })
                throw new InvalidOperationException("Active user not found for access token issuance.");
            claims.Add(new("token_use", "access_token"));
            claims.Add(new(UserTokenValidator.EpochClaim,
                (user.TokenInvalidBefore?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        }

        if (!string.IsNullOrWhiteSpace(claimsJson))
        {
            claims.Add(new("claims_json", claimsJson));
        }

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: resolvedAudience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_jwtOptions.AccessTokenLifetimeMinutes),
            signingCredentials: signingCredentialsProvider.GetSigningCredentials());

        token.Header[JwtHeaderParameterNames.Kid] = signingCredentialsProvider.KeyId;

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        logger.LogLuckInformation(
            "Access token issued. ClientId={ClientId} UserId={UserId} ExpiresAt={ExpiresAt} Outcome={Outcome}",
            [clientId, userId, token.ValidTo, "AccessTokenIssued"]);

        return await Task.FromResult(new TokenIssueResult(jwt, jti, token.ValidTo));
    }

    /// <summary>
    /// </summary>
    public async Task<string> IssueIdTokenAsync(
        string clientId,
        Guid userId,
        string? nonce,
        string accessToken,
        string? claimsJson = null,
        DateTimeOffset? authenticatedAt = null,
        string? acr = null,
        string? amr = null,
        CancellationToken ct = default)
    {
        var user = await userRepository.FindByIdAsync(userId, ct)
                   ?? throw new InvalidOperationException("User not found for id_token issuance.");
        var requestedClaims = OidcRequestedClaims.Parse(claimsJson);
        // 中文注释：id_token 通过 at_hash 与同时签发的 access token 绑定，该 access token 的 scope
        // 就是本次实际授予的 scope；用户 claim 只能在这些 scope 覆盖范围内返回（claims 参数不能越权）。
        var grantedScopes = ReadGrantedScopes(accessToken);

        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("client_id", clientId),
            new("token_use", "id_token"),
            new(UserTokenValidator.EpochClaim,
                (user.TokenInvalidBefore?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("at_hash", ComputeTokenHash(accessToken)),
        };

        if (OidcClaimEmissionPolicy.IsClaimAllowedByScopes("preferred_username", grantedScopes))
            claims.Add(new("preferred_username", user.Username));

        if (OidcClaimEmissionPolicy.IsClaimAllowedByScopes("name", grantedScopes))
            claims.Add(new("name", user.Nickname));

        if (authenticatedAt.HasValue)
            claims.Add(new("auth_time", authenticatedAt.Value.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));

        if (!string.IsNullOrWhiteSpace(acr))
            claims.Add(new("acr", acr));

        if (!string.IsNullOrWhiteSpace(amr))
        {
            foreach (var method in amr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new("amr", method));
            }
        }

        if (!string.IsNullOrWhiteSpace(user.Email)
            && OidcClaimEmissionPolicy.IsClaimAllowedByScopes("email", grantedScopes)
            && OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "email", user.Email)
            && (requestedClaims.RequestsIdTokenClaim("email") || requestedClaims.RequestsIdTokenClaim("email_verified")))
        {
            claims.Add(new("email", user.Email));
            claims.Add(new("email_verified", "false", ClaimValueTypes.Boolean));
        }

        if (!string.IsNullOrWhiteSpace(user.PhoneNumber)
            && OidcClaimEmissionPolicy.IsClaimAllowedByScopes("phone_number", grantedScopes)
            && OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "phone_number", user.PhoneNumber)
            && (requestedClaims.RequestsIdTokenClaim("phone_number") || requestedClaims.RequestsIdTokenClaim("phone_number_verified")))
        {
            claims.Add(new("phone_number", user.PhoneNumber));
            claims.Add(new("phone_number_verified", "false", ClaimValueTypes.Boolean));
        }

        if (OidcClaimEmissionPolicy.IsClaimAllowedByScopes("gender", grantedScopes)
            && OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "gender", user.Gender.ToString()))
            claims.Add(new("gender", user.Gender.ToString()));

        if (!string.IsNullOrWhiteSpace(user.Ethnicity)
            && OidcClaimEmissionPolicy.IsClaimAllowedByScopes("ethnicity", grantedScopes)
            && OidcClaimEmissionPolicy.ShouldEmitRequestedClaim(requestedClaims, "ethnicity", user.Ethnicity))
            claims.Add(new("ethnicity", user.Ethnicity));

        if (requestedClaims.RequestsIdTokenClaim("nickname")
            && OidcClaimEmissionPolicy.IsClaimAllowedByScopes("nickname", grantedScopes))
            claims.Add(new("nickname", user.Nickname));

        if (!string.IsNullOrWhiteSpace(nonce))
            claims.Add(new("nonce", nonce));

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: clientId,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_jwtOptions.AccessTokenLifetimeMinutes),
            signingCredentials: signingCredentialsProvider.GetSigningCredentials());

        token.Header[JwtHeaderParameterNames.Kid] = signingCredentialsProvider.KeyId;
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        logger.LogLuckInformation(
            "ID token issued. ClientId={ClientId} UserId={UserId} ExpiresAt={ExpiresAt} Outcome={Outcome}",
            [clientId, userId, token.ValidTo, "IdTokenIssued"]);

        return jwt;
    }

    /// <summary>
    /// </summary>
    public async Task<string> IssueRefreshTokenAsync(
        string clientId,
        Guid userId,
        string scope,
        CancellationToken ct = default)
    {
        var refreshLifetime = TimeSpan.FromMinutes(_jwtOptions.RefreshTokenLifetimeMinutes);
        var refreshToken = RefreshToken.Create(clientId, userId, scope, refreshLifetime);
        await refreshTokenRepository.AddAsync(refreshToken.Entity, ct);

        logger.LogLuckInformation(
            "Refresh token issued. ClientId={ClientId} UserId={UserId} ExpiresAt={ExpiresAt} Outcome={Outcome}",
            [clientId, userId, refreshToken.Entity.ExpiresAt, "RefreshTokenIssued"]);

        return refreshToken.RawToken;
    }

    /// <summary>
    /// 使用 refresh_token 轮换刷新访问令牌
    /// 主要流程
    /// 1. 查找 refresh_token；已轮换的 token 被再次出示时视为重放，撤销该用户在该客户端下的全部 refresh token
    /// 2. 校验过期、用户状态、安全策略与客户端归属
    /// 3. 先签发 access token（可能失败的步骤），再原子提交轮换，避免失败时客户端丢失会话
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(
        string refreshTokenString,
        string? clientId = null,
        CancellationToken ct = default)
    {
        var existingToken = await refreshTokenRepository.FindByTokenAsync(refreshTokenString, ct);

        if (existingToken is null)
            return RefreshFailure(clientId, "RefreshTokenNotFound", "Invalid refresh token.");

        if (existingToken.IsRevoked)
        {
            await RevokeFamilyOnReuseAsync(existingToken, ct);
            return RefreshFailure(clientId ?? existingToken.ClientId, "RefreshTokenRevoked", "Refresh token has been revoked.");
        }

        if (existingToken.ExpiresAt <= DateTimeOffset.UtcNow)
            return RefreshFailure(clientId ?? existingToken.ClientId, "RefreshTokenExpired", "Refresh token has expired.");

        var user = await userRepository.FindByIdAsync(existingToken.UserId, ct);
        if (user is null || !user.IsActive)
            return RefreshFailure(clientId ?? existingToken.ClientId, "UserInactive", "The user account is no longer active.");

        if (securityPolicyService is not null && !securityPolicyService.CheckUser(user).IsSuccess)
            return RefreshFailure(clientId ?? existingToken.ClientId, "UserPolicyRejected", "The user account is not allowed by security policy.");

        if (user.TokenInvalidBefore.HasValue && existingToken.CreatedAt <= user.TokenInvalidBefore.Value)
            return RefreshFailure(clientId ?? existingToken.ClientId, "RefreshTokenRevoked", "Refresh token has been revoked.");

        if (!string.IsNullOrWhiteSpace(clientId)
            && !string.Equals(existingToken.ClientId, clientId, StringComparison.Ordinal))
        {
            return RefreshFailure(clientId, "ClientMismatch", "Refresh token does not belong to the authenticated client.");
        }

        // 中文注释：access token 是无状态 JWT，先签发它；若因用户状态或资源 audience 变化而失败，
        // 旧 refresh token 尚未被轮换，客户端不会因此丢失会话，并返回 invalid_grant 而不是 500。
        string accessToken;
        try
        {
            accessToken = await IssueAccessTokenAsync(
                existingToken.ClientId,
                existingToken.Scope,
                null,
                existingToken.UserId,
                null,
                ct);
        }
        catch (InvalidOperationException)
        {
            return RefreshFailure(existingToken.ClientId, "AccessTokenIssueFailed", "Unable to issue an access token for this refresh token.");
        }

        // The database transaction atomically revokes the old token and stores
        // the replacement. A concurrent reuse of the same bearer value loses
        // the conditional update and never receives a new token.
        var newRefreshToken = RefreshToken.Create(
            existingToken.ClientId,
            existingToken.UserId,
            existingToken.Scope,
            TimeSpan.FromMinutes(_jwtOptions.RefreshTokenLifetimeMinutes));
        var rotatedToken = await refreshTokenRepository.RotateAsync(
            RefreshToken.Hash(refreshTokenString),
            existingToken.ClientId,
            newRefreshToken.Entity,
            ct);
        if (rotatedToken is null)
            return RefreshFailure(existingToken.ClientId, "RefreshTokenRotationFailed", "Refresh token has been revoked, expired, or already used.");

        await RecordRotationAsync(existingToken, ct);

        logger.LogLuckInformation(
            "Refresh token rotation succeeded. ClientId={ClientId} UserId={UserId} Outcome={Outcome}",
            [existingToken.ClientId, existingToken.UserId, "RefreshTokenRotated"]);

        return RefreshResult.Success(accessToken, newRefreshToken.RawToken);
    }

    /// <summary>
    /// </summary>
    public async Task RevokeRefreshTokenAsync(
        string refreshTokenString,
        CancellationToken ct = default)
    {
        await RevokeRefreshTokenAsync(refreshTokenString, null, ct);
    }

    /// <summary>
    /// </summary>
    public async Task RevokeRefreshTokenAsync(
        string refreshTokenString,
        string? clientId,
        CancellationToken ct = default)
    {
        var token = await refreshTokenRepository.FindByTokenAsync(refreshTokenString, ct);
        if (token is null)
        {
            logger.LogLuckWarning(
                "Refresh token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                ["RefreshTokenNotFound", "RefreshTokenNotFound"]);
            return;
        }

        if (!string.IsNullOrWhiteSpace(clientId)
            && !string.Equals(token.ClientId, clientId, StringComparison.Ordinal))
        {
            logger.LogLuckWarning(
                "Refresh token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                ["ClientMismatch", "ClientMismatch"]);
            return;
        }

        await refreshTokenRepository.RevokeAsync(token.Id, ct);

        logger.LogLuckInformation(
            "Refresh token revoked. ClientId={ClientId} UserId={UserId} Outcome={Outcome}",
            [token.ClientId, token.UserId, "RefreshTokenRevoked"]);
    }

    /// <summary>
    /// </summary>
    public async Task<bool> IsRefreshTokenOwnedByClientAsync(
        string refreshTokenString,
        string clientId,
        CancellationToken ct = default)
    {
        var token = await refreshTokenRepository.FindByTokenAsync(refreshTokenString, ct);
        return token is not null && string.Equals(token.ClientId, clientId, StringComparison.Ordinal);
    }

    /// <summary>
    /// </summary>
    public async Task RevokeAllUserTokensAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        // Refresh tokens are stateful and are revoked below. Access tokens are JWTs, so
        // record a per-user cutoff that introspection applies to every older token.
        var user = await userRepository.FindByIdAsync(userId, ct);
        if (user is not null)
        {
            user.InvalidateTokens(DateTimeOffset.UtcNow);
            await userRepository.UpdateAsync(user, ct);
        }

        await refreshTokenRepository.RevokeAllForUserAsync(userId, ct);

        logger.LogLuckInformation(
            "All refresh tokens revoked for user. UserId={UserId} Outcome={Outcome}",
            [userId, "RefreshTokensRevoked"]);
    }

    /// <summary>
    /// </summary>
    public async Task<TokenIntrospectionResult> IntrospectAsync(string token, CancellationToken ct = default)
    {
        return await IntrospectAsync(token, null, ct);
    }

    /// <summary>
    /// </summary>
    public async Task<TokenIntrospectionResult> IntrospectAsync(string token, string? clientId, CancellationToken ct = default)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(token))
            return TokenIntrospectionResult.Inactive();
        try
        {
            var principal = handler.ValidateToken(
                token,
                signingCredentialsProvider.CreateTokenValidationParameters(_jwtOptions.Issuer, null),
                out var validatedToken);

            var jwt = (JwtSecurityToken)validatedToken;
            // 中文注释：id_token 不是访问凭证，introspection 不应把它报告为 active（RFC 7662 §2.2）。
            if (string.Equals(principal.FindFirst("token_use")?.Value, "id_token", StringComparison.Ordinal))
                return TokenIntrospectionResult.Inactive();

            if (!await new UserTokenValidator(userRepository, tokenBlacklistRepository).IsActiveAsync(principal, ct))
                return TokenIntrospectionResult.Inactive();

            var tokenClientId = principal.FindFirst("client_id")?.Value;
            if (!string.IsNullOrWhiteSpace(clientId)
                && !string.Equals(tokenClientId, clientId, StringComparison.Ordinal))
            {
                return TokenIntrospectionResult.Inactive();
            }

            var subject = GetClaimValue(principal, JwtRegisteredClaimNames.Sub, ClaimTypes.NameIdentifier);
            return TokenIntrospectionResult.Success(
                subject,
                tokenClientId,
                principal.FindFirst("scope")?.Value,
                principal.FindFirst("claims_json")?.Value,
                new DateTimeOffset(jwt.ValidTo).ToUnixTimeSeconds(),
                jwt.Issuer,
                principal.FindFirst("token_use")?.Value);
        }
        catch (Exception)
        {
            return TokenIntrospectionResult.Inactive();
        }
    }

    /// <summary>
    /// </summary>
    public async Task RevokeAccessTokenAsync(string accessToken, CancellationToken ct = default)
    {
        await RevokeAccessTokenAsync(accessToken, null, ct);
    }

    /// <summary>
    /// </summary>
    public async Task RevokeAccessTokenAsync(string accessToken, string? clientId, CancellationToken ct = default)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(accessToken))
        {
            logger.LogLuckWarning(
                "Access token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                ["TokenNotReadable", "TokenNotReadable"]);
            return;
        }

        try
        {
            var principal = handler.ValidateToken(
                accessToken,
                signingCredentialsProvider.CreateTokenValidationParameters(_jwtOptions.Issuer, null),
                out var validatedToken);

            var jti = GetClaimValue(principal, JwtRegisteredClaimNames.Jti, ClaimTypes.SerialNumber);
            if (string.IsNullOrWhiteSpace(jti))
            {
                logger.LogLuckWarning(
                    "Access token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                    ["TokenIdentifierMissing", "TokenIdentifierMissing"]);
                return;
            }

            var tokenClientId = principal.FindFirst("client_id")?.Value;
            if (!string.IsNullOrWhiteSpace(clientId)
                && !string.Equals(tokenClientId, clientId, StringComparison.Ordinal))
            {
                logger.LogLuckWarning(
                    "Access token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                    ["ClientMismatch", "ClientMismatch"]);
                return;
            }

            var existing = await tokenBlacklistRepository.FindByJtiAsync(jti, ct);
            if (existing is not null)
            {
                logger.LogLuckWarning(
                    "Access token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                    ["TokenAlreadyRevoked", "TokenAlreadyRevoked"]);
                return;
            }

            var jwt = (JwtSecurityToken)validatedToken;
            var entry = TokenBlacklistEntry.Create(
                jti,
                principal.FindFirst("token_use")?.Value ?? "access_token",
                GetClaimValue(principal, JwtRegisteredClaimNames.Sub, ClaimTypes.NameIdentifier),
                jwt.ValidTo);

            await tokenBlacklistRepository.AddAsync(entry, ct);

            logger.LogLuckInformation(
                "Access token revoked. ClientId={ClientId} Subject={Subject} Outcome={Outcome}",
                [tokenClientId, GetClaimValue(principal, JwtRegisteredClaimNames.Sub, ClaimTypes.NameIdentifier), "AccessTokenRevoked"]);
        }
        catch
        {
            // OAuth revocation should not leak token validity details.
            logger.LogLuckWarning(
                "Access token revocation skipped. Reason={ReasonCode} Outcome={Outcome}",
                ["TokenValidationFailed", "TokenValidationFailed"]);
        }
    }

    /// <summary>
    /// 中文注释：已轮换的 refresh token 被再次出示，说明 token 可能已泄露（RFC 9700 §4.14.2）。
    /// 撤销该用户在该客户端下所有仍有效的 refresh token，攻击者与合法客户端都需要重新授权。
    /// 被显式撤销（revocation / 登出）的 token 没有轮换标记，只返回失败，不会波及其他会话。
    /// </summary>
    private async Task RevokeFamilyOnReuseAsync(RefreshToken presentedToken, CancellationToken ct)
    {
        var marker = BuildRotationMarker(presentedToken.TokenHash);
        if (!await tokenBlacklistRepository.ExistsActiveAsync(marker, DateTimeOffset.UtcNow, ct))
            return;

        var revokedCount = await refreshTokenRepository.RevokeAllForUserAndClientAsync(
            presentedToken.UserId,
            presentedToken.ClientId,
            ct);

        logger.LogLuckWarning(
            "Refresh token reuse detected; active refresh tokens for the user and client were revoked. ClientId={ClientId} UserId={UserId} RevokedCount={RevokedCount} Outcome={Outcome}",
            [presentedToken.ClientId, presentedToken.UserId, revokedCount, "RefreshTokenReuseDetected"]);
    }

    private async Task RecordRotationAsync(RefreshToken rotatedToken, CancellationToken ct)
    {
        try
        {
            await tokenBlacklistRepository.AddAsync(
                TokenBlacklistEntry.Create(
                    BuildRotationMarker(rotatedToken.TokenHash),
                    RefreshToken.RotationMarkerTokenType,
                    rotatedToken.UserId.ToString(),
                    rotatedToken.ExpiresAt),
                ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 中文注释：轮换本身已经成功提交；标记写入失败只会让这一枚旧 token 失去重放检测，不影响本次刷新。
            logger.LogLuckWarning(
                "Refresh token rotation marker could not be stored. ClientId={ClientId} UserId={UserId} Outcome={Outcome}",
                [rotatedToken.ClientId, rotatedToken.UserId, "RefreshTokenRotationMarkerFailed"]);
        }
    }

    private static string BuildRotationMarker(string tokenHash)
    {
        return RefreshToken.BuildRotationMarkerJti(tokenHash);
    }

    /// <summary>
    /// 中文注释：读取刚签发的 access token 中的 scope；读取失败时视为没有任何用户信息 scope。
    /// </summary>
    private static HashSet<string> ReadGrantedScopes(string accessToken)
    {
        try
        {
            var scope = new JwtSecurityTokenHandler().ReadJwtToken(accessToken)
                .Claims
                .FirstOrDefault(claim => string.Equals(claim.Type, "scope", StringComparison.Ordinal))?
                .Value;
            return OidcClaimEmissionPolicy.ParseScopes(scope);
        }
        catch (ArgumentException)
        {
            return OidcClaimEmissionPolicy.ParseScopes(null);
        }
    }

    private RefreshResult RefreshFailure(
        string? clientId,
        string reasonCode,
        string error)
    {
        logger.LogLuckWarning(
            "Refresh token operation failed. Reason={ReasonCode} Outcome={Outcome}",
            [reasonCode, reasonCode]);

        return RefreshResult.Failure(error);
    }

    private static string ComputeTokenHash(string token)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        var leftHalf = hash[..(hash.Length / 2)];
        return Base64UrlEncoder.Encode(leftHalf);
    }

    private static string? GetClaimValue(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private async Task<string> ResolveAudienceAsync(string scope, string? requestedAudience, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedAudience))
            return requestedAudience;

        var scopes = scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var resourceScopeNames = scopes
            .Where(scopeName => !IsIdentityScope(scopeName))
            .ToArray();

        var resources = await apiResourceRepository.FindByAudiencesAsync(resourceScopeNames, ct);
        var activeAudiences = resources
            .Where(resource => resource.IsActive)
            .Select(resource => resource.Audience)
            .ToHashSet(StringComparer.Ordinal);

        string? resolved = null;
        foreach (var scopeName in resourceScopeNames)
        {
            if (!activeAudiences.Contains(scopeName))
                continue;

            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = scopeName;
                continue;
            }

            if (!string.Equals(resolved, scopeName, StringComparison.Ordinal))
                throw new InvalidOperationException("Requested scopes span multiple audiences. Please request one resource audience per token.");
        }

        return resolved ?? _jwtOptions.DefaultAudience;
    }

    private static bool IsIdentityScope(string scope)
    {
        return string.Equals(scope, "openid", StringComparison.Ordinal)
            || string.Equals(scope, "profile", StringComparison.Ordinal)
            || string.Equals(scope, "email", StringComparison.Ordinal)
            || string.Equals(scope, "phone", StringComparison.Ordinal)
            || string.Equals(scope, "address", StringComparison.Ordinal)
            || string.Equals(scope, "offline_access", StringComparison.Ordinal);
    }

}

public record TokenIssueResult(string AccessToken, string Jti, DateTime ExpiresAtUtc);

public record RefreshResult(
    bool IsSuccess,
    string? AccessToken,
    string? RefreshToken,
    string? Error)
{
    public static RefreshResult Success(string accessToken, string refreshToken)
        => new(true, accessToken, refreshToken, null);

    public static RefreshResult Failure(string error)
        => new(false, null, null, error);
}

public record TokenIntrospectionResult(
    bool Active,
    string? Subject,
    string? ClientId,
    string? Scope,
    string? ClaimsJson,
    long? Exp,
    string? Issuer,
    string? TokenUse)
{
    public static TokenIntrospectionResult Inactive() => new(false, null, null, null, null, null, null, null);

    public static TokenIntrospectionResult Success(string? subject, string? clientId, string? scope, string? claimsJson, long? exp, string? issuer, string? tokenUse)
        => new(true, subject, clientId, scope, claimsJson, exp, issuer, tokenUse);
}
