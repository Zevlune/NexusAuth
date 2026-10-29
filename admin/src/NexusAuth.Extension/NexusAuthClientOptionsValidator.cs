using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Extension;

internal sealed class NexusAuthClientOptionsValidator : IValidateOptions<NexusAuthClientOptions>
{
    public ValidateOptionsResult Validate(string? name, NexusAuthClientOptions options)
    {
        var error = NexusAuthConfigurationValidation.ValidateOptions(options);
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }
}

internal static class NexusAuthConfigurationValidation
{
    public static string? ValidateOptions(NexusAuthClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Authority))
            return "Authority is required.";
        if (!IsAbsoluteHttpUri(options.Authority, options.RequireHttpsMetadata, allowQuery: false))
        {
            return options.RequireHttpsMetadata
                ? "Authority must be an absolute HTTPS URI without user info, query, or fragment."
                : "Authority must be an absolute HTTP(S) URI without user info, query, or fragment.";
        }
        if (string.IsNullOrWhiteSpace(options.ClientId))
            return "ClientId is required.";
        if (!Enum.IsDefined(options.ClientAuthenticationMethod))
            return "ClientAuthenticationMethod is invalid.";
        if (options.ClientAuthenticationMethod is not ClientAuthenticationMethod.None
            && string.IsNullOrEmpty(options.ClientSecret))
        {
            return "ClientSecret is required for the selected client authentication method.";
        }

        return null;
    }

    public static string? ValidateConfiguration(
        OpenIdConnectConfiguration configuration,
        NexusAuthClientOptions options)
    {
        if (!string.Equals(configuration.Issuer, options.Authority, StringComparison.Ordinal))
            return "Discovery issuer must equal the configured Authority.";

        foreach (var endpoint in new[]
                 {
                     configuration.AuthorizationEndpoint,
                     configuration.TokenEndpoint,
                     configuration.UserInfoEndpoint,
                     configuration.JwksUri,
                     configuration.IntrospectionEndpoint,
                     configuration.RevocationEndpoint,
                     configuration.EndSessionEndpoint
                 })
        {
            if (!string.IsNullOrEmpty(endpoint)
                && !IsAbsoluteHttpUri(endpoint, options.RequireHttpsMetadata, allowQuery: true))
            {
                return options.RequireHttpsMetadata
                    ? "Discovery endpoints must be absolute HTTPS URIs without user info or fragments."
                    : "Discovery endpoints must be absolute HTTP(S) URIs without user info or fragments.";
            }
        }

        return null;
    }

    public static bool IsAbsoluteHttpUri(string value, bool requireHttps, bool allowQuery)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && (!requireHttps || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && (allowQuery || string.IsNullOrEmpty(uri.Query));
    }
}
