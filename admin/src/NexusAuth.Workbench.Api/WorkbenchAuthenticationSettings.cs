using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api;

internal sealed record WorkbenchAuthenticationSettings(
    string Authority,
    string Audience,
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    string PostLogoutRedirectUri,
    string Scope,
    bool GatewayEnabled,
    bool SignOutProvider,
    bool RequireHttpsMetadata,
    IReadOnlySet<string> AllowedBearerClientIds)
{
    public static WorkbenchAuthenticationSettings FromConfiguration(IConfiguration configuration)
    {
        var gatewayEnabled = configuration.IsWorkbenchGatewayEnabled();
        var scope = EnsureResourceScope(configuration["Auth:Scope"], configuration["Auth:Audience"]);
        ValidateConfiguration(configuration, gatewayEnabled, scope);

        var credentials = WorkbenchClientCredentials.FromConfiguration(
            configuration["Auth:ClientId"], configuration["Auth:ClientSecret"]);
        var allowedClientIds = configuration.GetSection("Auth:AllowedBearerClientIds").GetChildren()
            .Select(entry => entry.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        allowedClientIds.Add(credentials.ClientId);

        return new WorkbenchAuthenticationSettings(
            Authority: configuration["Auth:Authority"]!.Trim().TrimEnd('/'),
            Audience: configuration["Auth:Audience"]!,
            ClientId: credentials.ClientId,
            ClientSecret: credentials.ClientSecret,
            RedirectUri: configuration["Auth:RedirectUri"] ?? string.Empty,
            PostLogoutRedirectUri: configuration["Auth:PostLogoutRedirectUri"] ?? string.Empty,
            Scope: scope ?? string.Empty,
            GatewayEnabled: gatewayEnabled,
            SignOutProvider: !bool.TryParse(configuration["Auth:SignOutProvider"], out var signOutProvider) || signOutProvider,
            RequireHttpsMetadata: bool.TryParse(configuration["Auth:RequireHttpsMetadata"], out var requireHttpsMetadata) && requireHttpsMetadata,
            AllowedBearerClientIds: allowedClientIds);
    }

    private static void ValidateConfiguration(IConfiguration configuration, bool gatewayEnabled, string? scope)
    {
        var errors = new List<string>();
        RequireValue(configuration["Auth:Authority"], "Authority", "AUTHORITY", errors);
        RequireValue(configuration["Auth:Audience"], "Audience", "AUDIENCE", errors);
        RequireValue(configuration["Auth:ClientId"], "ClientId", "CLIENT_ID", errors);
        RequireValue(configuration["Auth:ClientSecret"], "ClientSecret", "CLIENT_SECRET", errors);
        if (!gatewayEnabled)
        {
            RequireValue(configuration["Auth:RedirectUri"], "RedirectUri", "REDIRECT_URI", errors);
            RequireValue(configuration["Auth:PostLogoutRedirectUri"], "PostLogoutRedirectUri", "POST_LOGOUT_REDIRECT_URI", errors);
            RequireValue(scope, "Scope", "SCOPE", errors);
        }
        if (!Uri.TryCreate(configuration["Auth:Authority"], UriKind.Absolute, out var authority)
            || authority.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment))
        {
            errors.Add("Auth:Authority (NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY) must be an absolute HTTP(S) URI without user info, query, or fragment.");
        }
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    private static void RequireValue(string? value, string key, string environmentSuffix, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"Auth:{key} (NEXUSAUTH_WORKBENCH_AUTH_{environmentSuffix}) is required.");
    }

    private static string? EnsureResourceScope(string? scope, string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(resourceName))
            return scope;

        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!scopes.Contains(resourceName.Trim(), StringComparer.Ordinal))
            scopes.Add(resourceName.Trim());
        return string.Join(' ', scopes);
    }
}
