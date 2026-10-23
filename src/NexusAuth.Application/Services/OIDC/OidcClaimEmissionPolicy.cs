namespace NexusAuth.Application.Services.OIDC;

public static class OidcClaimEmissionPolicy
{
    // 中文注释：OIDC Core §5.4 定义的 scope -> claim 映射。claims 请求参数只能在已授予 scope 的范围内
    // 选择 claim，不能绕过 scope 拿到额外的用户信息；ethnicity 属于敏感扩展信息，同样要求 profile。
    private static readonly Dictionary<string, string> ClaimScopes = new(StringComparer.Ordinal)
    {
        ["email"] = "email",
        ["email_verified"] = "email",
        ["phone_number"] = "phone",
        ["phone_number_verified"] = "phone",
        ["address"] = "address",
        ["name"] = "profile",
        ["family_name"] = "profile",
        ["given_name"] = "profile",
        ["middle_name"] = "profile",
        ["nickname"] = "profile",
        ["preferred_username"] = "profile",
        ["profile"] = "profile",
        ["picture"] = "profile",
        ["website"] = "profile",
        ["gender"] = "profile",
        ["birthdate"] = "profile",
        ["zoneinfo"] = "profile",
        ["locale"] = "profile",
        ["updated_at"] = "profile",
        ["ethnicity"] = "profile",
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
        return claimRequest?.Matches(actualValue) ?? true;
    }

    /// <summary>
    /// 中文注释：判断某个用户 claim 是否被已授予的 scope 覆盖。
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
