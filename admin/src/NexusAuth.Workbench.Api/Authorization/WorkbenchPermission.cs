namespace NexusAuth.Workbench.Api.Authorization;

/// <summary>
/// 描述授予 Workbench 用户的 HTTP 权限。
/// </summary>
/// <param name="HttpMethod">具体 HTTP 方法，或使用 <c>*</c> 匹配所有方法。</param>
/// <param name="Path">
/// 实际请求路径、端点原始路由模板，或使用 <c>*</c> 匹配所有路径。
/// </param>
public sealed record WorkbenchPermission(string HttpMethod, string Path);
