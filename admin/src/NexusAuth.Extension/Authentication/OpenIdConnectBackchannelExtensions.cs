using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>为标准 OIDC 后台请求配置客户端认证方式。</summary>
public static class OpenIdConnectBackchannelExtensions
{
    /// <summary>
    /// 从指定认证方案的原生 Options 读取凭据，使用 client_secret_basic 发送表单请求。
    /// 仅适配凭据的传输方式，授权码兑换和响应校验仍由标准 OIDC 中间件负责。
    /// </summary>
    public static IHttpClientBuilder UseOpenIdConnectClientSecretBasic(
        this IHttpClientBuilder builder, string authenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        // HttpClientFactory 创建并回收 handler；不另行注册或手工释放共享实例。
        return builder.AddHttpMessageHandler(provider => new ClientSecretBasicHandler(
            provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(), authenticationScheme));
    }

    private sealed class ClientSecretBasicHandler(
        IOptionsMonitor<OpenIdConnectOptions> options, string authenticationScheme) : DelegatingHandler
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
}
