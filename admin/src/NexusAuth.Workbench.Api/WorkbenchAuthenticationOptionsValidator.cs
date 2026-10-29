using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api;

internal sealed class WorkbenchAuthenticationOptionsValidator : IValidateOptions<WorkbenchAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkbenchAuthenticationOptions options)
    {
        var errors = new List<string>();
        Require(options.Authority, "Authority", "AUTHORITY");
        Require(options.Audience, "Audience", "AUDIENCE");
        Require(options.ClientId, "ClientId", "CLIENT_ID");
        Require(options.ClientSecret, "ClientSecret", "CLIENT_SECRET");
        Require(options.RedirectUri, "RedirectUri", "REDIRECT_URI");
        Require(options.PostLogoutRedirectUri, "PostLogoutRedirectUri", "POST_LOGOUT_REDIRECT_URI");
        Require(options.Scope, "Scope", "SCOPE");
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
            || authority.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment))
        {
            errors.Add("Auth:Authority (NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY) must be an absolute HTTP(S) URI without user info, query, or fragment.");
        }
        else if (options.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Auth:Authority (NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY) must use HTTPS when "
                + "Auth:RequireHttpsMetadata (NEXUSAUTH_WORKBENCH_AUTH_REQUIRE_HTTPS_METADATA) is true.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

        void Require(string? value, string key, string environmentSuffix)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"Auth:{key} (NEXUSAUTH_WORKBENCH_AUTH_{environmentSuffix}) is required.");
        }
    }
}
