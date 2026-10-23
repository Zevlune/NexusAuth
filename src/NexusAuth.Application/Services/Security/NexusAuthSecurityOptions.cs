namespace NexusAuth.Application.Services.Security;

public class NexusAuthSecurityOptions
{
    public List<string> ClientWhitelist { get; set; } = [];

    public List<string> ClientBlacklist { get; set; } = [];

    public List<string> UserWhitelist { get; set; } = [];

    public List<string> UserBlacklist { get; set; } = [];

    /// <summary>
    /// Persistent password-attempt lockout settings. Keeping these values in
    /// configuration lets deployments tune them without changing the domain
    /// model or exposing account state in the login response.
    /// </summary>
    public int LoginFailureLimit { get; set; } = 5;

    public int LoginLockoutMinutes { get; set; } = 5;

    /// <summary>
    /// Secure policy for the Host SSO and antiforgery cookies: SameAsRequest
    /// (default, keeps local HTTP working) or Always (recommended in production,
    /// especially behind a TLS-terminating proxy).
    /// </summary>
    public string CookieSecurePolicy { get; set; } = "SameAsRequest";
}
