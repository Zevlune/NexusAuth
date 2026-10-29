namespace NexusAuth.Workbench.Api.Options;

/// <summary>Configuration shared by the standard OIDC handler and BFF session services.</summary>
public sealed class WorkbenchAuthenticationOptions
{
    public const string SectionName = "Auth";

    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string PostLogoutRedirectUri { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public bool SignOutProvider { get; set; } = true;
    public bool RequireHttpsMetadata { get; set; } = true;

    internal void Normalize()
    {
        Authority = Authority?.Trim().TrimEnd('/') ?? string.Empty;
        Audience = Audience?.Trim() ?? string.Empty;
        ClientId = ClientId?.Trim() ?? string.Empty;
        ClientSecret = ClientSecret?.Trim() ?? string.Empty;
        RedirectUri = RedirectUri?.Trim() ?? string.Empty;
        PostLogoutRedirectUri = PostLogoutRedirectUri?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(Scope))
        {
            var scopes = Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToList();
            if (!string.IsNullOrEmpty(Audience) && !scopes.Contains(Audience, StringComparer.Ordinal))
                scopes.Add(Audience);
            Scope = string.Join(' ', scopes);
        }
    }
}
