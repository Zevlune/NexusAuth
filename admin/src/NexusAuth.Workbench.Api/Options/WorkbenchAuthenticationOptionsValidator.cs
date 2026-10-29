using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api.Options;

internal sealed class WorkbenchAuthenticationOptionsValidator : IValidateOptions<WorkbenchAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkbenchAuthenticationOptions options)
    {
        var errors = new List<string>();
        Require(options.Authority, "Authority");
        Require(options.Audience, "Audience");
        Require(options.ClientId, "ClientId");
        Require(options.ClientSecret, "ClientSecret");
        Require(options.RedirectUri, "RedirectUri");
        Require(options.PostLogoutRedirectUri, "PostLogoutRedirectUri");
        Require(options.Scope, "Scope");
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
            || authority.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment))
        {
            errors.Add("Auth:Authority must be an absolute HTTP(S) URI without user info, query, or fragment.");
        }
        else if (options.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Auth:Authority must use HTTPS when Auth:RequireHttpsMetadata is true.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

        void Require(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"Auth:{key} is required in appsettings configuration.");
        }
    }
}
