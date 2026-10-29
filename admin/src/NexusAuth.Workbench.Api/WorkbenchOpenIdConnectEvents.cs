using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api;

/// <summary>Adapts standard OIDC authentication to Workbench's JSON login and session contract.</summary>
public sealed class WorkbenchOpenIdConnectEvents(
    WorkbenchAuthenticationSettings settings,
    OpenIdConnectFlowState flows) : OpenIdConnectEvents
{
    private const string ExchangeStartedKey = "Workbench.Oidc.ExchangeStarted";

    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        WorkbenchBasicAuthenticationHandler.RequireEndpoint(new Uri(context.ProtocolMessage.IssuerAddress), settings.RequireHttpsMetadata);
        context.ProtocolMessage.RedirectUri = settings.RedirectUri;
        return Task.CompletedTask;
    }

    public override Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        WorkbenchBasicAuthenticationHandler.RequireEndpoint(new Uri(context.ProtocolMessage.IssuerAddress), settings.RequireHttpsMetadata);
        context.ProtocolMessage.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
        return Task.CompletedTask;
    }

    public override Task MessageReceived(MessageReceivedContext context) => flows.ValidateAsync(context);

    public override Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        context.HttpContext.Items[ExchangeStartedKey] = true;
        return Task.CompletedTask; // The framework redeems the code and validates its protocol response.
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        // The framework permits unsigned ID tokens from a TLS-protected code endpoint. Workbench
        // keeps its stricter existing policy: the ID token must carry an RS256 signature.
        if (!string.Equals(context.SecurityToken.Issuer, settings.Authority, StringComparison.Ordinal)
            || string.IsNullOrEmpty(context.SecurityToken.RawSignature)
            || !string.Equals(context.SecurityToken.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            throw new SecurityTokenValidationException("A signed RS256 ID token is required.");
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
