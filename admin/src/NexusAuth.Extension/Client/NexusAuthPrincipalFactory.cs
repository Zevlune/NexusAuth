using System.Security.Claims;

namespace NexusAuth.Extension;

/// <summary>由已验证的 ID Token 生成精简的会话 Principal，避免把全部 claim 写入 Cookie。</summary>
public static class NexusAuthPrincipalFactory
{
    /// <summary>保留 sub 与显示名；sub 缺失时拒绝登录。</summary>
    public static ClaimsPrincipal Create(ClaimsPrincipal validatedPrincipal, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(validatedPrincipal);
        var subject = validatedPrincipal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
            throw new InvalidOperationException("The ID token does not contain a subject.");
        var name = validatedPrincipal.FindFirstValue("name")
            ?? validatedPrincipal.FindFirstValue("preferred_username") ?? subject;
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Name, name)],
            authenticationType));
    }
}
