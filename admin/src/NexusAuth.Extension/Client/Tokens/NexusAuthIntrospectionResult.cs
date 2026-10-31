namespace NexusAuth.Extension;

/// <summary>内省端点返回的、会话校验所需的字段。</summary>
public sealed record NexusAuthIntrospectionResult(bool Active, string? Subject, string? ClientId);
