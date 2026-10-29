using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>
/// Validates required Workbench OIDC settings through the options pipeline at application startup.
/// </summary>
internal sealed class WorkbenchAuthOptionsValidator : IValidateOptions<WorkbenchAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkbenchAuthOptions opts)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(opts.Authority))
            errors.Add("Authority is required.");
        else if (!IsValidAuthority(opts.Authority))
            errors.Add("Authority must be an absolute HTTP(S) base URI without user info, query, or fragment.");
        if (string.IsNullOrWhiteSpace(opts.ClientId))
            errors.Add("ClientId is required.");
        if (string.IsNullOrWhiteSpace(opts.RedirectUri))
            errors.Add("RedirectUri is required.");
        if (string.IsNullOrWhiteSpace(opts.PostLogoutRedirectUri))
            errors.Add("PostLogoutRedirectUri is required.");
        if (string.IsNullOrWhiteSpace(opts.Scope))
            errors.Add("Scope is required.");

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }

    private static bool IsValidAuthority(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
