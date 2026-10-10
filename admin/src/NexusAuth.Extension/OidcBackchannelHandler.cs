using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>
/// Redirects server-side provider requests from the public authority to its internal address.
/// Requests outside the authority origin and path prefix pass through unchanged.
/// </summary>
public sealed class OidcBackchannelHandler : DelegatingHandler
{
    private readonly Uri authority;
    private readonly Uri? backchannelAuthority;
    private readonly string authorityPath;
    private readonly string backchannelPath;

    /// <summary>Creates the handler from the configured public and backchannel authorities.</summary>
    public OidcBackchannelHandler(IOptions<WorkbenchAuthOptions> options)
        : this(options.Value.Authority, options.Value.BackchannelAuthority)
    {
    }

    /// <summary>Creates the handler with explicit public and optional internal authorities.</summary>
    public OidcBackchannelHandler(string authority, string? backchannelAuthority)
    {
        this.authority = ParseBaseUri(authority, nameof(authority));
        this.backchannelAuthority = string.IsNullOrWhiteSpace(backchannelAuthority)
            ? null
            : ParseBaseUri(backchannelAuthority, nameof(backchannelAuthority));
        authorityPath = TrimPath(this.authority.AbsolutePath);
        this.backchannelPath = this.backchannelAuthority is null
            ? string.Empty
            : TrimPath(this.backchannelAuthority.AbsolutePath);
    }

    /// <summary>Validates an optional absolute HTTP(S) backchannel base URI.</summary>
    public static void ValidateBackchannelAuthority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        _ = ParseBaseUri(value, nameof(value));
    }

    /// <summary>Maps a matching public-authority URI to the backchannel while retaining its path suffix and query.</summary>
    public static Uri MapUri(Uri requestUri, string authority, string backchannelAuthority)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        var handler = new OidcBackchannelHandler(authority, backchannelAuthority);
        return handler.MapUri(requestUri);
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } requestUri && backchannelAuthority is not null)
            request.RequestUri = MapUri(requestUri);

        return base.SendAsync(request, cancellationToken);
    }

    private Uri MapUri(Uri requestUri)
    {
        if (backchannelAuthority is null || !SameOrigin(requestUri, authority))
            return requestUri;

        var requestPath = requestUri.AbsolutePath;
        string suffix;
        if (authorityPath.Length == 0)
        {
            suffix = requestPath;
        }
        else if (string.Equals(requestPath, authorityPath, StringComparison.Ordinal))
        {
            suffix = string.Empty;
        }
        else if (requestPath.StartsWith(authorityPath + "/", StringComparison.Ordinal))
        {
            suffix = requestPath[authorityPath.Length..];
        }
        else
        {
            return requestUri;
        }

        var mappedPath = backchannelPath + suffix;
        if (mappedPath.Length == 0)
            mappedPath = "/";

        var mappedUri = backchannelAuthority.GetLeftPart(UriPartial.Authority) + mappedPath + requestUri.Query;
        return new Uri(mappedUri, UriKind.Absolute);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static string TrimPath(string path) => path.TrimEnd('/');

    private static Uri ParseBaseUri(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Authority must be an absolute HTTP(S) base URI without user info, query, or fragment.", parameterName);
        }

        return uri;
    }
}
