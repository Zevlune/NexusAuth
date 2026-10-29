using System.Globalization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Options;

namespace NexusAuth.Workbench.Api.Authentication.Events;

/// <summary>连接标准 OIDC 事件与 Workbench 的浏览器绑定、JSON 登录返回和会话策略。</summary>
public sealed class WorkbenchOpenIdConnectEvents(
    IOptions<WorkbenchAuthenticationOptions> authenticationOptions,
    OpenIdConnectFlowState flows) : OpenIdConnectEvents
{
    private readonly WorkbenchAuthenticationOptions settings = authenticationOptions.Value;

    private const string ExchangeStartedKey = "Workbench.Oidc.ExchangeStarted";

    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        // 中间件负责构造协议请求；这里仅校验跳转端点并补入配置的完整回调地址。
        OpenIdConnectEndpointValidator.Validate(new Uri(context.ProtocolMessage.IssuerAddress), settings.RequireHttpsMetadata);
        context.ProtocolMessage.RedirectUri = settings.RedirectUri;
        return Task.CompletedTask;
    }

    public override Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        OpenIdConnectEndpointValidator.Validate(new Uri(context.ProtocolMessage.IssuerAddress), settings.RequireHttpsMetadata);
        context.ProtocolMessage.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
        return Task.CompletedTask;
    }

    // 在框架继续校验回调前，原子消费扩展流程并确认来自发起登录的浏览器。
    public override Task MessageReceived(MessageReceivedContext context) => flows.ValidateAsync(context);

    public override Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        context.HttpContext.Items[ExchangeStartedKey] = true;
        return Task.CompletedTask; // 只记录错误分类标记，授权码兑换和响应校验继续交给框架。
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        // Workbench 保留更严格的策略：即使令牌来自受 TLS 保护的授权码端点，也要求带 RS256 签名。
        // 这是当前应用的额外限制，不是所有 OIDC 客户端都应强制采用的通用默认值。
        if (!string.Equals(context.SecurityToken.Issuer, settings.Authority, StringComparison.Ordinal)
            || string.IsNullOrEmpty(context.SecurityToken.RawSignature)
            || !string.Equals(context.SecurityToken.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            throw new SecurityTokenValidationException("A signed RS256 ID token is required.");
        return Task.CompletedTask;
    }

    public override Task TicketReceived(TicketReceivedContext context)
    {
        // 本 BFF 必须持有 refresh token 和有效过期时间，才能维持独立的 24 小时会话。
        if (context.Properties is null || string.IsNullOrEmpty(context.Properties.GetTokenValue("refresh_token"))
            || !DateTimeOffset.TryParse(context.Properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
        {
            context.Fail("The Workbench session requires a refresh token.");
            return Task.CompletedTask;
        }
        context.Principal = WorkbenchPrincipalFactory.Create(context.Principal!);
        context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24);
        context.Properties.IsPersistent = true;
        context.Properties.AllowRefresh = true;
        return Task.CompletedTask;
    }

    public override Task AccessDenied(AccessDeniedContext context)
    {
        context.Response.Redirect(ProviderError("access_denied", context.Request.Query["error_description"]));
        context.HandleResponse();
        return Task.CompletedTask;
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        // 保留前端约定的错误跳转；只有已通过流程绑定校验的 Provider 错误才能透传分类。
        var validated = context.HttpContext.Items.ContainsKey(OpenIdConnectFlowState.ValidatedKey);
        var error = context.Request.Query["error"].ToString();
        var redirect = validated && context.Failure is OpenIdConnectProtocolException && !string.IsNullOrEmpty(error)
            ? ProviderError(error, context.Request.Query["error_description"])
            : settings.PostLogoutRedirectUri.TrimEnd('/') + "/login?error="
                + (context.HttpContext.Items.ContainsKey(ExchangeStartedKey)
                    ? "token_exchange_failed" : "invalid_callback");
        context.Response.Redirect(redirect);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private string ProviderError(string error, string? description) =>
        QueryHelpers.AddQueryString(settings.Authority.TrimEnd('/') + "/oauth/error",
            new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description });
}
