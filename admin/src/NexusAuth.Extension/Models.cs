using System.Net;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace NexusAuth.Extension;

/// <summary>Selects how the OAuth client authenticates at protected endpoints.</summary>
public enum ClientAuthenticationMethod
{
    /// <summary>Sends RFC 6749 HTTP Basic credentials.</summary>
    ClientSecretBasic,

    /// <summary>Sends client credentials in the form body.</summary>
    ClientSecretPost,

    /// <summary>Sends only the client identifier, for public clients.</summary>
    None
}

/// <summary>Configures the NexusAuth OAuth and OpenID Connect backchannel client.</summary>
public sealed class NexusAuthClientOptions
{
    /// <summary>Gets or sets the issuer and discovery base URI.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Gets or sets the registered OAuth client identifier.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Gets or sets whether discovery, JWKS, and OAuth endpoints require HTTPS.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Gets or sets the client authentication method.</summary>
    public ClientAuthenticationMethod ClientAuthenticationMethod { get; set; } =
        ClientAuthenticationMethod.ClientSecretBasic;
}

/// <summary>Defines the backchannel operations used by NexusAuth clients.</summary>
public interface INexusAuthClient
{
    /// <summary>Gets the cached OpenID Connect discovery configuration and signing keys.</summary>
    Task<OpenIdConnectConfiguration> FetchConfigurationAsync(CancellationToken ct);

    /// <summary>Exchanges an authorization code and PKCE verifier for tokens.</summary>
    Task<NexusAuthTokenResult> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct);

    /// <summary>Refreshes a token set.</summary>
    Task<NexusAuthTokenResult> RefreshTokensAsync(string refreshToken, CancellationToken ct);

    /// <summary>Introspects an access token.</summary>
    Task<AccessTokenIntrospectionResult> IntrospectAccessTokenAsync(string accessToken, CancellationToken ct);

    /// <summary>Revokes a token.</summary>
    Task RevokeTokenAsync(string token, string tokenTypeHint, CancellationToken ct);
}

/// <summary>Represents tokens returned by a token endpoint.</summary>
public sealed record NexusAuthTokenResult(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    int ExpiresIn);

/// <summary>Represents access-token introspection data.</summary>
public sealed record AccessTokenIntrospectionResult(bool Active, string? Subject, string? ClientId);

/// <summary>Reports an OAuth protocol error without exposing credentials or response bodies.</summary>
public sealed class NexusAuthProtocolException : Exception
{
    /// <summary>Creates a protocol exception with a safe OAuth error code and HTTP status.</summary>
    public NexusAuthProtocolException(string error, HttpStatusCode statusCode)
        : this(error, statusCode, null)
    {
    }

    internal NexusAuthProtocolException(
        string error,
        HttpStatusCode statusCode,
        Exception? innerException)
        : base(
            $"The authorization server returned protocol error '{error}' (HTTP {(int)statusCode}).",
            innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }

    /// <summary>Gets the OAuth error code, or a safe generic code for an invalid response.</summary>
    public string Error { get; }

    /// <summary>Gets the HTTP status code returned by the authorization server.</summary>
    public HttpStatusCode StatusCode { get; }
}
