using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>
/// 中文：在应用启动解析时验证必需的 Workbench OIDC 配置。
/// English: Validates required Workbench OIDC settings when this service is resolved during application startup.
/// </summary>
public class ValidatedWorkbenchAuthOptions
{
    /// <summary>
    /// 中文：创建验证器并检查当前 Workbench OIDC 配置。
    /// English: Creates the validator and checks the current Workbench OIDC configuration.
    /// </summary>
    /// <param name="options">中文：由配置系统提供的 Workbench OIDC 选项。English: The Workbench OIDC options supplied by the configuration system.</param>
    /// <exception cref="InvalidOperationException">中文：一个或多个配置项缺失或 Authority 无效。English: One or more settings are missing or Authority is invalid.</exception>
    public ValidatedWorkbenchAuthOptions(IOptions<WorkbenchAuthOptions> options)
    {
        var opts = options.Value;
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

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    private static bool IsValidAuthority(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
