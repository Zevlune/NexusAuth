using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

internal sealed class ClientSecretBasicHandler(
    IOptionsMonitor<OpenIdConnectOptions> options,
    string authenticationScheme) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // 延迟到请求阶段读取，避免配置 OIDC Backchannel 时再次构造同一份 Options。
        var settings = options.Get(authenticationScheme);
        OpenIdConnectEndpointValidator.Validate(request.RequestUri, settings.RequireHttpsMetadata);
        if (request.Method == HttpMethod.Post
            && request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
        {
            var form = QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(ct));
            // 标准中间件默认把凭据放在表单中；改用 Basic 后必须删除它们，避免混用两种认证。
            form.Remove("client_id");
            form.Remove("client_secret");
            var previous = request.Content;
            request.Content = new FormUrlEncodedContent(form.SelectMany(pair =>
                pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? string.Empty))));
            previous.Dispose();
            // OAuth Basic 要求先分别对 ID 和 secret 做表单编码，再拼接并 Base64 编码。
            var credentials = $"{WebUtility.UrlEncode(settings.ClientId)}:{WebUtility.UrlEncode(settings.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        return await base.SendAsync(request, ct);
    }
}
