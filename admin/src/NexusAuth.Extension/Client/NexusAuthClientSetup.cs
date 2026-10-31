namespace NexusAuth.Extension;

/// <summary>
/// 注册阶段即需确定的认证方案名称与事件类型。注册后以单例提供给 SDK 组件读取，不应再修改。
/// </summary>
public sealed class NexusAuthClientSetup
{
    /// <summary>默认方案：按请求是否带 Authorization 头选择 Bearer 或 Cookie。</summary>
    public string DefaultScheme { get; set; } = "NexusAuth";

    /// <summary>浏览器会话 Cookie 方案。</summary>
    public string CookieScheme { get; set; } = "NexusAuth.Cookie";

    /// <summary>对接 NexusAuth.Host 的 OIDC 方案。</summary>
    public string OpenIdConnectScheme { get; set; } = "NexusAuth.OpenIdConnect";

    /// <summary>API 调用使用的 JWT Bearer 方案。</summary>
    public string BearerScheme { get; set; } = "Bearer";

    /// <summary>OIDC 授权码兑换与令牌操作共用的命名 HttpClient。</summary>
    public string BackchannelHttpClientName { get; set; } = "NexusAuth.Oidc";

    /// <summary>是否注册 JWT Bearer 方案；纯浏览器会话应用可关闭。</summary>
    public bool EnableBearer { get; set; } = true;

    /// <summary>Cookie 事件类型，须继承 <see cref="NexusAuthCookieAuthenticationEvents"/>。</summary>
    public Type CookieEventsType { get; set; } = typeof(NexusAuthCookieAuthenticationEvents);

    /// <summary>OIDC 事件类型，须继承 <see cref="NexusAuthOpenIdConnectEvents"/>。</summary>
    public Type OpenIdConnectEventsType { get; set; } = typeof(NexusAuthOpenIdConnectEvents);

    /// <summary>JWT Bearer 事件类型，须继承 <see cref="NexusAuthJwtBearerEvents"/>。</summary>
    public Type JwtBearerEventsType { get; set; } = typeof(NexusAuthJwtBearerEvents);

    internal void Validate()
    {
        foreach (var (value, name) in new[]
                 {
                     (DefaultScheme, nameof(DefaultScheme)), (CookieScheme, nameof(CookieScheme)),
                     (OpenIdConnectScheme, nameof(OpenIdConnectScheme)), (BearerScheme, nameof(BearerScheme)),
                     (BackchannelHttpClientName, nameof(BackchannelHttpClientName)),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"NexusAuthClientSetup.{name} is required.");
        }

        RequireDerived(CookieEventsType, typeof(NexusAuthCookieAuthenticationEvents), nameof(CookieEventsType));
        RequireDerived(OpenIdConnectEventsType, typeof(NexusAuthOpenIdConnectEvents), nameof(OpenIdConnectEventsType));
        RequireDerived(JwtBearerEventsType, typeof(NexusAuthJwtBearerEvents), nameof(JwtBearerEventsType));
    }

    private static void RequireDerived(Type? type, Type baseType, string name)
    {
        if (type is null || !baseType.IsAssignableFrom(type) || type.IsAbstract)
            throw new InvalidOperationException($"NexusAuthClientSetup.{name} must be a concrete {baseType.Name}.");
    }
}
