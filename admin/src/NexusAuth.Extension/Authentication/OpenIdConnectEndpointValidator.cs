namespace NexusAuth.Extension;

/// <summary>校验发现文档提供的 OIDC 地址，可用于后台请求和浏览器跳转。</summary>
public static class OpenIdConnectEndpointValidator
{
    /// <summary>拒绝相对地址、非 HTTP(S)、内嵌凭据和片段；默认要求 HTTPS。</summary>
    public static void Validate(Uri? endpoint, bool requireHttps = true)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || (requireHttps && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                "OIDC endpoints must use absolute HTTP(S) URIs without user info or fragments, and HTTPS when required.");
        }
    }
}
