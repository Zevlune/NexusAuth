using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace NexusAuth.Extension;

/// <summary>Adds one-time browser binding evidence to standard OpenID Connect login flows.</summary>
public sealed class OpenIdConnectFlowState
{
    /// <summary>The authentication-properties key that carries the server-side flow identifier.</summary>
    public const string PropertyKey = "NexusAuth.LoginFlow";

    /// <summary>The HTTP-context item key set after a callback flow is validated.</summary>
    public const string ValidatedKey = "NexusAuth.LoginFlow.Validated";

    private readonly IFlowStateStore flows;
    private readonly OpenIdConnectFlowStateOptions flowOptions;
    private readonly IOptionsMonitor<OpenIdConnectOptions> oidcOptions;

    /// <summary>Creates a scoped login-flow service.</summary>
    public OpenIdConnectFlowState(
        IFlowStateStore flows,
        IOptions<OpenIdConnectFlowStateOptions> flowOptions,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions)
    {
        this.flows = flows ?? throw new ArgumentNullException(nameof(flows));
        this.flowOptions = flowOptions?.Value
            ?? throw new ArgumentNullException(nameof(flowOptions));
        this.oidcOptions = oidcOptions
            ?? throw new ArgumentNullException(nameof(oidcOptions));
    }

    /// <summary>Creates and stores a browser-bound flow before the OIDC challenge starts.</summary>
    public async Task BeginAsync(
        HttpContext context,
        AuthenticationProperties properties,
        string scheme,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ct.ThrowIfCancellationRequested();

        var flow = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var binding = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await flows.AddAsync(flow, new FlowState(Hash(binding)), ct).ConfigureAwait(false);

        properties.Items[PropertyKey] = flow;
        var secure = ResolveSecure(context, scheme);
        context.Response.Cookies.Append(
            CookieName(flow, secure),
            binding,
            CookieOptions(secure));
    }

    /// <summary>Removes a pending flow and its browser-binding cookie.</summary>
    public async Task CancelAsync(
        HttpContext context,
        AuthenticationProperties properties,
        string scheme,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ct.ThrowIfCancellationRequested();

        if (properties.Items.TryGetValue(PropertyKey, out var flow)
            && IsFlow(flow))
        {
            await flows.RemoveAsync(flow, ct).ConfigureAwait(false);
            var secure = ResolveSecure(context, scheme);
            context.Response.Cookies.Delete(
                CookieName(flow, secure),
                CookieOptions(secure));
        }

        properties.Items.Remove(PropertyKey);
    }

    /// <summary>Atomically consumes and validates the flow carried by an OIDC callback.</summary>
    public async Task ValidateAsync(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var properties = context.Properties;
        if (properties is null
            || !properties.Items.TryGetValue(PropertyKey, out var flow)
            || !IsFlow(flow))
        {
            context.Fail("invalid_callback");
            return;
        }

        var secure = ResolveSecure(context.HttpContext, context.Scheme.Name);
        var cookieName = CookieName(flow, secure);
        if (!context.Request.Cookies.TryGetValue(cookieName, out var binding)
            || binding.Length != 64
            || !binding.All(Uri.IsHexDigit)
            || await flows.TryTakeAsync(
                flow,
                Hash(binding),
                context.HttpContext.RequestAborted).ConfigureAwait(false) is null)
        {
            context.Fail("invalid_callback");
            return;
        }

        context.HttpContext.Items[ValidatedKey] = true;
        properties.Items.Remove(PropertyKey);
        context.Response.Cookies.Delete(cookieName, CookieOptions(secure));
    }

    private bool ResolveSecure(HttpContext context, string scheme) =>
        context.Request.IsHttps
        || oidcOptions.Get(scheme).CorrelationCookie.SecurePolicy
            == CookieSecurePolicy.Always;

    private CookieOptions CookieOptions(bool secure) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = flowOptions.Lifetime,
        IsEssential = true,
    };

    private static string CookieName(string flow, bool secure) =>
        $"{(secure ? "__Host-" : ".")}NexusAuth.Oidc.{flow}";

    private static bool IsFlow([NotNullWhen(true)] string? flow) =>
        flow is { Length: 64 } && flow.All(Uri.IsHexDigit);

    private static string Hash(string binding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
}
