using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

internal sealed class NexusAuthClientOptionsValidator(
    string sectionPath,
    bool bearerEnabled,
    bool redisConnectionRegistered,
    IOptions<RedisFlowStateOptions> flowRedisOptions) : IValidateOptions<NexusAuthClientOptions>
{
    public ValidateOptionsResult Validate(string? name, NexusAuthClientOptions options)
    {
        var errors = new List<string>();
        Require(options.Authority, "Authority");
        if (bearerEnabled)
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
            errors.Add($"{sectionPath}:Authority must be an absolute HTTP(S) URI without user info, query, or fragment.");
        }
        else if (options.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{sectionPath}:Authority must use HTTPS when {sectionPath}:RequireHttpsMetadata is true.");
        }

        RequireAbsolute(options.RedirectUri, "RedirectUri");
        RequireAbsolute(options.PostLogoutRedirectUri, "PostLogoutRedirectUri");
        if (!string.IsNullOrEmpty(options.Frontend.BaseUri))
            RequireAbsolute(options.Frontend.BaseUri, "Frontend:BaseUri");
        RequireLocalPath(options.Frontend.CallbackPath, "Frontend:CallbackPath");
        RequireLocalPath(options.Frontend.LoginPath, "Frontend:LoginPath");
        if (options.ValidAlgorithms.Length == 0)
            errors.Add($"{sectionPath}:ValidAlgorithms must contain at least one algorithm.");

        ValidateSession(options.Session);
        ValidateCluster(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

        void Require(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{sectionPath}:{key} is required in appsettings configuration.");
        }

        void RequireAbsolute(string value, string key)
        {
            if (!string.IsNullOrEmpty(value)
                && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                errors.Add($"{sectionPath}:{key} must be an absolute HTTP(S) URI.");
        }

        void RequireLocalPath(string? value, string key)
        {
            if (!NexusAuthPaths.IsLocalPath(value))
                errors.Add($"{sectionPath}:{key} must be a local path starting with '/'.");
        }

        void ValidateSession(NexusAuthSessionOptions session)
        {
            if (string.IsNullOrWhiteSpace(session.CookieName))
                errors.Add($"{sectionPath}:Session:CookieName is required.");
            if (session.CookieSecurePolicy is not (CookieSecurePolicy.Always or CookieSecurePolicy.SameAsRequest))
                errors.Add($"{sectionPath}:Session:CookieSecurePolicy must be SameAsRequest or Always.");
            if (session.Lifetime <= TimeSpan.Zero)
                errors.Add($"{sectionPath}:Session:Lifetime must be positive.");
            if (session.RefreshBeforeExpiry < TimeSpan.Zero || session.RefreshBeforeExpiry > TimeSpan.FromMinutes(30))
                errors.Add($"{sectionPath}:Session:RefreshBeforeExpiry must be between 0 and 30 minutes.");
        }

        void ValidateCluster(NexusAuthClientOptions value)
        {
            if (value.TokenRefresh.Provider is not (NexusAuthProviders.InMemory or NexusAuthProviders.Redis))
                errors.Add($"{sectionPath}:TokenRefresh:Provider must be exactly InMemory or Redis.");
            if (value.TokenRefresh.ResultLifetime < TimeSpan.FromSeconds(1)
                || value.TokenRefresh.ResultLifetime > TimeSpan.FromMinutes(5))
                errors.Add($"{sectionPath}:TokenRefresh:ResultLifetime must be between 1 second and 5 minutes.");

            var dataProtection = value.DataProtection;
            if (dataProtection.Provider is not (NexusAuthProviders.None or NexusAuthProviders.Redis or NexusAuthProviders.FileSystem))
                errors.Add($"{sectionPath}:DataProtection:Provider must be exactly None, Redis or FileSystem.");
            if (dataProtection.Provider != NexusAuthProviders.None && string.IsNullOrWhiteSpace(dataProtection.ApplicationName))
                errors.Add($"{sectionPath}:DataProtection:ApplicationName is required so every instance shares one key ring.");
            if (dataProtection.Provider == NexusAuthProviders.FileSystem && string.IsNullOrWhiteSpace(dataProtection.KeyPath))
                errors.Add($"{sectionPath}:DataProtection:KeyPath is required for the FileSystem provider.");

            if (string.IsNullOrWhiteSpace(value.Redis.KeyPrefix) || value.Redis.KeyPrefix.IndexOfAny(['{', '}']) >= 0)
                errors.Add($"{sectionPath}:Redis:KeyPrefix must be non-empty and must not contain braces.");
            var redisRequired = value.TokenRefresh.Provider == NexusAuthProviders.Redis
                || dataProtection.Provider == NexusAuthProviders.Redis;
            if (redisRequired && !redisConnectionRegistered
                && string.IsNullOrWhiteSpace(value.Redis.Configuration)
                && string.IsNullOrWhiteSpace(flowRedisOptions.Value.Configuration))
                errors.Add($"{sectionPath}:Redis:Configuration is required when a Redis provider is selected and no IConnectionMultiplexer is registered.");
        }
    }
}

internal static class NexusAuthPaths
{
    /// <summary>仅接受站内路径，拒绝 //host 与 /\host 形式的开放重定向。</summary>
    public static bool IsLocalPath(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] == '/'
        && (value.Length == 1 || (value[1] != '/' && value[1] != '\\'))
        && !value.Contains('\r') && !value.Contains('\n');
}
