namespace NexusAuth.Workbench.Api;

public static class WorkbenchAuthenticationDefaults
{
    public const string Scheme = CookieScheme;
    public const string CookieScheme = "Workbench.Cookie";
    public const string OpenIdConnectScheme = "Workbench.OpenIdConnect";
}
