using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
}
