namespace NexusAuth.Application.Clients;

public record ClientAuthenticationInput(
    string? ClientId,
    string? ClientSecret,
    string? ClientAssertionType,
    string? ClientAssertion,
    string? AssertionAudience = null,
    string? TokenEndpointAuthMethod = null,
    IReadOnlyList<string>? AssertionAudiences = null)
{
    /// <summary>
    /// 中文注释：client assertion 允许的 aud 集合（RFC 7523 §3：issuer 或 token endpoint URL）。
    /// 优先使用 AssertionAudiences，兼容只设置单个 AssertionAudience 的旧调用方。
    /// </summary>
    public IReadOnlyList<string> GetAcceptedAssertionAudiences()
    {
        if (AssertionAudiences is { Count: > 0 })
            return [.. AssertionAudiences.Where(audience => !string.IsNullOrWhiteSpace(audience)).Distinct(StringComparer.Ordinal)];

        return string.IsNullOrWhiteSpace(AssertionAudience) ? [] : [AssertionAudience];
    }
}
