using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace NexusAuth.Extension;

/// <summary>
/// Holds Workbench client credentials with one configuration normalization rule
/// shared by bootstrap provisioning and OIDC backchannel authentication.
/// </summary>
public sealed class WorkbenchClientCredentials
{
    private WorkbenchClientCredentials(string clientId, string clientSecret)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    /// <summary>Gets the normalized OAuth client identifier.</summary>
    public string ClientId { get; }

    /// <summary>Gets the normalized OAuth client secret.</summary>
    public string ClientSecret { get; }

    /// <summary>
    /// Trims surrounding whitespace from both credentials. Whitespace inside a
    /// credential remains part of its value.
    /// </summary>
    public static WorkbenchClientCredentials FromConfiguration(string? clientId, string? clientSecret)
    {
        return new WorkbenchClientCredentials(
            clientId?.Trim() ?? string.Empty,
            clientSecret?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// Creates an RFC 6749 Basic authorization header. Each credential is
    /// form-urlencoded before being joined and Base64 encoded.
    /// </summary>
    public AuthenticationHeaderValue CreateBasicAuthenticationHeader()
    {
        var encodedClientId = WebUtility.UrlEncode(ClientId);
        var encodedClientSecret = WebUtility.UrlEncode(ClientSecret);
        var credentialBytes = Encoding.UTF8.GetBytes($"{encodedClientId}:{encodedClientSecret}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
    }
}
