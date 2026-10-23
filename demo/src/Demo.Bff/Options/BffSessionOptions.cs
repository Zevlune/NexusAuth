namespace Demo.Bff.Options;

public class BffSessionOptions
{
    public string CookieName { get; set; } = ".Demo.Bff.Session";

    public int CookieLifetimeMinutes { get; set; } = 10;

    /// <summary>
    /// 绑定 OIDC state 的短期 Cookie 名称。基于会话 Cookie 名称派生，
    /// 避免同一主机上的多个 BFF（Cookie 不区分端口）互相覆盖。
    /// </summary>
    public string StateCookieName => $"{CookieName}.OidcState";
}
