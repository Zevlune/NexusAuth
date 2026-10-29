namespace NexusAuth.Workbench.Api.Authentication;

public static class WorkbenchAuthenticationDefaults
{
    public const string Scheme = CookieScheme;
    public const string CookieScheme = "Workbench.Cookie";
    public const string OpenIdConnectScheme = "Workbench.OpenIdConnect";
}
