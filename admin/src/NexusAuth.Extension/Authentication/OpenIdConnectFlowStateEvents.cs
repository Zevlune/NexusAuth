using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace NexusAuth.Extension;

/// <summary>
/// 为标准 OIDC 事件提供一次性流程绑定与跳转端点校验；不设置应用的回调地址、会话或错误页面。
/// 注册为 Scoped 并设置 OpenIdConnectOptions.EventsType 后使用；自定义事件可继承此类。
/// </summary>
public class OpenIdConnectFlowStateEvents(OpenIdConnectFlowState flows) : OpenIdConnectEvents
{
    /// <summary>回调继续交给框架前，原子消费流程并验证发起登录的浏览器。</summary>
    public override async Task MessageReceived(MessageReceivedContext context)
    {
        await flows.ValidateAsync(context);
        if (context.Result is null)
            await base.MessageReceived(context);
    }

    /// <summary>按当前 OIDC 方案的 HTTPS 要求校验授权端点。</summary>
    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        ValidateEndpoint(context);
        return base.RedirectToIdentityProvider(context);
    }

    /// <summary>按当前 OIDC 方案的 HTTPS 要求校验 Provider 登出端点。</summary>
    public override Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        ValidateEndpoint(context);
        return base.RedirectToIdentityProviderForSignOut(context);
    }

    private static void ValidateEndpoint(RedirectContext context) =>
        OpenIdConnectEndpointValidator.Validate(
            Uri.TryCreate(context.ProtocolMessage.IssuerAddress, UriKind.Absolute, out var endpoint) ? endpoint : null,
            context.Options.RequireHttpsMetadata);
}
