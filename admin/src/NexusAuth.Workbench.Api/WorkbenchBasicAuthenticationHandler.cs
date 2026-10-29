using System.Net.Http.Headers;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace NexusAuth.Workbench.Api;

/// <summary>Preserves the registered Basic authentication while the standard handler redeems codes.</summary>
public sealed class WorkbenchBasicAuthenticationHandler(IOptions<WorkbenchAuthenticationOptions> authenticationOptions) : DelegatingHandler
{
    private readonly WorkbenchAuthenticationOptions settings = authenticationOptions.Value;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequireEndpoint(request.RequestUri, settings.RequireHttpsMetadata);
        if (request.Method == HttpMethod.Post
            && request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
        {
            var body = QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(ct));
            // The middleware writes credentials into its form by default. Send one authentication
            // method only; this adapter does not redeem codes or process token responses.
            body.Remove("client_id");
            body.Remove("client_secret");
            var previous = request.Content;
            request.Content = new FormUrlEncodedContent(body.SelectMany(pair =>
                pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? string.Empty))));
            previous.Dispose();
            var credentials = $"{WebUtility.UrlEncode(settings.ClientId)}:{WebUtility.UrlEncode(settings.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
        return await base.SendAsync(request, ct);
    }

    internal static void RequireEndpoint(Uri? endpoint, bool requireHttps)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || (requireHttps && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("OIDC endpoints must use absolute HTTPS URIs without user info or fragments.");
    }
}
