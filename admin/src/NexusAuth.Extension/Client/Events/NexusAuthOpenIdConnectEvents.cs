using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NexusAuth.Extension;

/// <summary>
/// 在流程绑定与端点校验之上，补入 NexusAuth BFF 会话策略：完整回调地址、签名算法限制、
/// 必须持有 refresh token、精简 Principal 及前端错误跳转。应用可继承并覆盖。
/// </summary>
public class NexusAuthOpenIdConnectEvents(
    IOptions<NexusAuthClientOptions> clientOptions,
    NexusAuthClientSetup setup,
    OpenIdConnectFlowState flows) : OpenIdConnectFlowStateEvents(flows)
{
    private const string ExchangeStartedKey = "NexusAuth.Oidc.ExchangeStarted";

    /// <summary>当前客户端配置。</summary>
    protected NexusAuthClientOptions Settings { get; } = clientOptions.Value;

    /// <inheritdoc />
    public override async Task RedirectToIdentityProvider(RedirectContext context)
    {
        // 中间件负责构造协议请求和 PKCE；这里仅校验跳转端点并补入配置的完整回调地址，
        // 使反向代理或集群节点的内部地址不会出现在 redirect_uri 中。
        await base.RedirectToIdentityProvider(context);
        context.ProtocolMessage.RedirectUri = Settings.RedirectUri;
    }

    /// <inheritdoc />
    public override async Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        await base.RedirectToIdentityProviderForSignOut(context);
        context.ProtocolMessage.PostLogoutRedirectUri = Settings.PostLogoutRedirectUri;
    }

    /// <inheritdoc />
    public override Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        context.HttpContext.Items[ExchangeStartedKey] = true;
        return Task.CompletedTask; // 只记录错误分类标记，授权码兑换和响应校验继续交给框架。
    }

    /// <inheritdoc />
    public override Task TokenValidated(TokenValidatedContext context)
    {
        // 即使令牌来自受 TLS 保护的令牌端点，也要求 Issuer 一致且使用允许的非对称签名。
        if (!string.Equals(context.SecurityToken.Issuer, Settings.Authority, StringComparison.Ordinal)
            || string.IsNullOrEmpty(context.SecurityToken.RawSignature)
            || !Settings.ValidAlgorithms.Contains(context.SecurityToken.Header.Alg, StringComparer.Ordinal))
            throw new SecurityTokenValidationException("A signed ID token with an allowed algorithm is required.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task TicketReceived(TicketReceivedContext context)
    {
        // 会话依赖 refresh token 和有效过期时间续期，缺失时拒绝建立会话（需要 offline_access）。
        if (context.Properties is null || string.IsNullOrEmpty(context.Properties.GetTokenValue("refresh_token"))
            || !DateTimeOffset.TryParse(context.Properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
        {
            context.Fail("The session requires a refresh token.");
            return Task.CompletedTask;
        }
        context.Principal = CreatePrincipal(context);
        var now = DateTimeOffset.UtcNow;
        context.Properties.IssuedUtc = now;
        context.Properties.ExpiresUtc = now.Add(Settings.Session.Lifetime);
        context.Properties.IsPersistent = true;
        context.Properties.AllowRefresh = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task AccessDenied(AccessDeniedContext context)
    {
        context.Response.Redirect(ProviderError("access_denied", context.Request.Query["error_description"]));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task RemoteFailure(RemoteFailureContext context)
    {
        // 只有已通过流程绑定校验的 Provider 错误才透传分类，其余统一返回前端登录页。
        var validated = context.HttpContext.Items.ContainsKey(OpenIdConnectFlowState.ValidatedKey);
        var error = context.Request.Query["error"].ToString();
        var redirect = validated && context.Failure is OpenIdConnectProtocolException && !string.IsNullOrEmpty(error)
            ? ProviderError(error, context.Request.Query["error_description"])
            : QueryHelpers.AddQueryString(Settings.FrontendBaseUri + Settings.Frontend.LoginPath, "error",
                context.HttpContext.Items.ContainsKey(ExchangeStartedKey) ? "token_exchange_failed" : "invalid_callback");
        context.Response.Redirect(redirect);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>生成写入会话 Cookie 的 Principal；覆盖时应保留 NameIdentifier。</summary>
    protected virtual System.Security.Claims.ClaimsPrincipal CreatePrincipal(TicketReceivedContext context) =>
        NexusAuthPrincipalFactory.Create(context.Principal!, setup.CookieScheme);

    private string ProviderError(string error, string? description) =>
        QueryHelpers.AddQueryString(Settings.Authority + "/oauth/error",
            new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description });
}
