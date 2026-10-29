using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api;

/// <summary>Adapts standard OIDC authentication to Workbench's JSON login and session contract.</summary>
public sealed class WorkbenchOpenIdConnectEvents(
    WorkbenchAuthenticationSettings settings,
    INexusAuthClient client,
    IFlowStateStore flows,
    IOptions<WorkbenchFlowStateOptions> flowOptions,
    IOptionsMonitor<CookieAuthenticationOptions> cookies) : OpenIdConnectEvents
{
    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        context.ProtocolMessage.RedirectUri = settings.RedirectUri;
        return Task.CompletedTask;
    }

    public override Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        context.ProtocolMessage.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
        return Task.CompletedTask;
    }

    public override async Task MessageReceived(MessageReceivedContext context)
    {
        // Properties have already been unprotected by the framework. Keep server-side atomic
        // consumption as an additional guard against simultaneous/replayed callbacks.
        var properties = context.Properties;
        string? flow = null;
        properties?.Items.TryGetValue(WorkbenchLoginFlow.PropertyKey, out flow);
        var options = WorkbenchLoginFlow.CookieOptions(context.HttpContext, cookies, flowOptions.Value.Lifetime);
        if (flow is null || flow.Length != 64 || !flow.All(Uri.IsHexDigit)
            || !context.Request.Cookies.TryGetValue(WorkbenchLoginFlow.CookieName(flow, options.Secure), out var binding)
            || binding.Length != 64
            || await flows.TryTakeAsync(flow, WorkbenchLoginFlow.Hash(binding), context.HttpContext.RequestAborted) is null)
        {
            context.Fail("invalid_callback");
            return;
        }
        context.HttpContext.Items[WorkbenchLoginFlow.ValidatedKey] = true;
        properties!.Items.Remove(WorkbenchLoginFlow.PropertyKey);
        context.Response.Cookies.Delete(WorkbenchLoginFlow.CookieName(flow, options.Secure), options);
    }

    public override async Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        // NexusAuth registers this client with client_secret_basic. Adapt only HTTP redemption;
        // state, PKCE generation, nonce and ID-token validation stay with the standard handler.
        context.HttpContext.Items[WorkbenchLoginFlow.ExchangeStartedKey] = true;
        var request = context.TokenEndpointRequest!;
        var tokens = await client.ExchangeCodeAsync(request.Code, request.GetParameter("code_verifier"),
            request.RedirectUri, context.HttpContext.RequestAborted);
        context.HandleCodeRedemption(new OpenIdConnectMessage
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            IdToken = tokens.IdToken,
            ExpiresIn = tokens.ExpiresIn.ToString(CultureInfo.InvariantCulture),
            TokenType = "Bearer",
        });
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        // The framework permits unsigned ID tokens from a TLS-protected code endpoint. Workbench
        // keeps its stricter existing policy: the ID token must carry an RS256 signature.
        if (string.IsNullOrEmpty(context.SecurityToken.RawSignature)
            || !string.Equals(context.SecurityToken.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            throw new SecurityTokenValidationException("A signed RS256 ID token is required.");
        // HandleCodeRedemption tells the framework that the caller also owns protocol-response
        // validation. Invoke its standard validator with the nonce read from its protected cookie.
        context.Options.ProtocolValidator.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
        {
            ClientId = context.Options.ClientId,
            ProtocolMessage = context.TokenEndpointResponse,
            ValidatedIdToken = context.SecurityToken,
            Nonce = context.Nonce,
        });
        return Task.CompletedTask;
    }

    public override Task TicketReceived(TicketReceivedContext context)
    {
        // Offline sessions are a Workbench policy, not a requirement of the generic token client.
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
        var validated = context.HttpContext.Items.ContainsKey(WorkbenchLoginFlow.ValidatedKey);
        var error = context.Request.Query["error"].ToString();
        var redirect = validated && context.Failure is OpenIdConnectProtocolException && !string.IsNullOrEmpty(error)
            ? ProviderError(error, context.Request.Query["error_description"])
            : settings.PostLogoutRedirectUri.TrimEnd('/') + "/login?error="
                + (context.HttpContext.Items.ContainsKey(WorkbenchLoginFlow.ExchangeStartedKey)
                    ? "token_exchange_failed" : "invalid_callback");
        context.Response.Redirect(redirect);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private string ProviderError(string error, string? description) =>
        QueryHelpers.AddQueryString(settings.Authority.TrimEnd('/') + "/oauth/error",
            new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description });
}
