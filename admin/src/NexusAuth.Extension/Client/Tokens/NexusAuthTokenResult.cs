namespace NexusAuth.Extension;

/// <summary>令牌端点的成功响应。</summary>
public sealed record NexusAuthTokenResult(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);
