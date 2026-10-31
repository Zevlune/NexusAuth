namespace NexusAuth.Workbench.Api.Authentication;

/// <summary>Workbench 沿用的方案与 Cookie 名称，避免升级 SDK 后已有会话和反向代理配置失效。</summary>
public static class WorkbenchAuthenticationDefaults
{
    public const string Scheme = "Workbench";
    public const string BearerScheme = "Bearer";
    public const string CookieScheme = "Workbench.Cookie";
    public const string OpenIdConnectScheme = "Workbench.OpenIdConnect";
    public const string BackchannelHttpClient = "Workbench.Oidc";
    public const string SessionCookieName = ".NexusAuth.Workbench";
}
