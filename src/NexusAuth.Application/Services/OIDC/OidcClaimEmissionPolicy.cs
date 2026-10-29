namespace NexusAuth.Application.Services.OIDC;

using NexusAuth.Shared.OAuth;

public static class OidcClaimEmissionPolicy
{
    // 中文注释：OIDC Core §5.4 / §5.5 定义了 scope -> claim 映射以及单项 claim 请求。
    // 本项目的保守策略仍要求所有用户 claim 同时受已授予 scope 限制，避免 claims 参数扩大数据权限；
    // ethnicity 属于敏感扩展信息，同样要求 profile。
    private static readonly Dictionary<string, string> ClaimScopes = new(StringComparer.Ordinal)
    {
        ["email"] = OidcScopes.Email,
        ["email_verified"] = OidcScopes.Email,
        ["phone_number"] = OidcScopes.Phone,
        ["phone_number_verified"] = OidcScopes.Phone,
        ["address"] = OidcScopes.Address,
        ["name"] = OidcScopes.Profile,
        ["family_name"] = OidcScopes.Profile,
        ["given_name"] = OidcScopes.Profile,
        ["middle_name"] = OidcScopes.Profile,
        ["nickname"] = OidcScopes.Profile,
        ["preferred_username"] = OidcScopes.Profile,
        ["profile"] = OidcScopes.Profile,
        ["picture"] = OidcScopes.Profile,
        ["website"] = OidcScopes.Profile,
        ["gender"] = OidcScopes.Profile,
        ["birthdate"] = OidcScopes.Profile,
        ["zoneinfo"] = OidcScopes.Profile,
        ["locale"] = OidcScopes.Profile,
        ["updated_at"] = OidcScopes.Profile,
        ["ethnicity"] = OidcScopes.Profile,
    };

    /// <summary>
    /// 判断某个 claim 是否应该按当前 OIDC claims 请求返回给客户端。
    /// 主要调用方：userinfo 端点与 id_token 组装逻辑。
    /// </summary>
    public static bool ShouldEmitRequestedClaim(OidcRequestedClaims requestedClaims, string claimName, string? actualValue)
    {
        if (!requestedClaims.RequestsClaim(claimName))
            return false;

        var claimRequest = requestedClaims.GetClaimRequest(claimName);
        return claimRequest?.Matches(actualValue, GetActualScalarKind(claimName)) ?? true;
    }

    /// <summary>
    /// Determines whether an explicit ID Token request for a claim matches the actual value.
    /// </summary>
    public static bool ShouldEmitIdTokenClaim(
        OidcRequestedClaims requestedClaims,
        string claimName,
        string? actualValue)
    {
        return requestedClaims.IdTokenClaimRequests.TryGetValue(claimName, out var claimRequest)
            && claimRequest.Matches(actualValue, GetActualScalarKind(claimName));
    }

    /// <summary>
    /// Determines whether an explicit UserInfo request for a claim matches the actual value.
    /// </summary>
    public static bool ShouldEmitUserInfoClaim(
        OidcRequestedClaims requestedClaims,
        string claimName,
        string? actualValue)
    {
        return requestedClaims.UserInfoClaimRequests.TryGetValue(claimName, out var claimRequest)
            && claimRequest.Matches(actualValue, GetActualScalarKind(claimName));
    }

    private static OidcClaimScalarKind GetActualScalarKind(string claimName) =>
        claimName switch
        {
            "email_verified" or "phone_number_verified" => OidcClaimScalarKind.Boolean,
            "auth_time" or "updated_at" => OidcClaimScalarKind.Number,
            _ => OidcClaimScalarKind.String,
        };

    /// <summary>
    /// 中文注释：按本项目的保守 scope gate，判断某个用户 claim 是否被已授予的 scope 覆盖。
    /// sub 始终允许；未登记映射的 claim 一律不允许通过 claims 参数获取。
    /// 主要调用方：userinfo 端点与 id_token 组装逻辑。
    /// </summary>
    public static bool IsClaimAllowedByScopes(string claimName, IReadOnlySet<string> grantedScopes)
    {
        if (string.Equals(claimName, "sub", StringComparison.Ordinal))
            return true;

        return ClaimScopes.TryGetValue(claimName, out var requiredScope)
            && grantedScopes.Contains(requiredScope);
    }

    /// <summary>
    /// 中文注释：把空格分隔的 scope 字符串解析成集合。
    /// </summary>
    public static HashSet<string> ParseScopes(string? scope)
    {
        return scope?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
    }
}
