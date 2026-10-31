namespace NexusAuth.Extension;

/// <summary>BFF 会话所需的令牌操作：刷新、内省与撤销，均使用标准 OIDC 方案的发现文档与后台客户端。</summary>
public interface INexusAuthTokenClient
{
    /// <summary>以 refresh_token 授权换取新令牌。</summary>
    Task<NexusAuthTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct);

    /// <summary>内省 Access Token 的当前状态。</summary>
    Task<NexusAuthIntrospectionResult> IntrospectAccessTokenAsync(string accessToken, CancellationToken ct);

    /// <summary>撤销 Access Token 或 Refresh Token。</summary>
    Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct);
}
