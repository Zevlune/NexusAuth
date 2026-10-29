using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OpenIdConnectFlowStateTests
{
    private const string OidcScheme = "oidc";
    private const string Issuer = "https://issuer.example.test";

    [Fact]
    public async Task Scoped_helper_begins_a_flow_with_properties_and_a_secure_binding_cookie()
    {
        using var provider = CreateProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<OpenIdConnectFlowState>();
        var sameScope = firstScope.ServiceProvider.GetRequiredService<OpenIdConnectFlowState>();
        var second = secondScope.ServiceProvider.GetRequiredService<OpenIdConnectFlowState>();

        Assert.Same(first, sameScope);
        Assert.NotSame(first, second);

        var httpContext = CreateHttpContext(firstScope.ServiceProvider);
        var properties = new AuthenticationProperties { RedirectUri = "/signin-oidc" };

        await first.BeginAsync(httpContext, properties, OidcScheme);

        var flow = Assert.IsType<string>(
            properties.Items[OpenIdConnectFlowState.PropertyKey]);
        Assert.Equal(64, flow.Length);
        Assert.All(flow, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.Equal("/signin-oidc", properties.RedirectUri);

        var cookie = ReadCookie(httpContext);
        Assert.Equal($"__Host-NexusAuth.Oidc.{flow}", cookie.Name);
        Assert.Equal(64, cookie.Value.Length);
        Assert.All(cookie.Value, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.NotEqual(flow, cookie.Value);

        var header = cookie.Header.ToLowerInvariant();
        Assert.Contains("httponly", header);
        Assert.Contains("secure", header);
        Assert.Contains("samesite=lax", header);
        Assert.Contains("path=/", header);
        Assert.Contains("max-age=600", header);
    }

    [Fact]
    public async Task Correct_callback_validates_once_and_replay_fails()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var flows = scope.ServiceProvider.GetRequiredService<OpenIdConnectFlowState>();
        var beginContext = CreateHttpContext(scope.ServiceProvider);
        var properties = new AuthenticationProperties();

        await flows.BeginAsync(beginContext, properties, OidcScheme);
        var flow = Assert.IsType<string>(
            properties.Items[OpenIdConnectFlowState.PropertyKey]);
        var cookie = ReadCookie(beginContext);

        var firstCallback = await CreateCallbackAsync(scope.ServiceProvider, properties, cookie);
        await flows.ValidateAsync(firstCallback.Context);

        Assert.Null(firstCallback.Context.Result);
        Assert.True(Assert.IsType<bool>(
            firstCallback.Context.HttpContext.Items[OpenIdConnectFlowState.ValidatedKey]));
        Assert.DoesNotContain(OpenIdConnectFlowState.PropertyKey, firstCallback.Properties.Items.Keys);
        Assert.True(IsCookieDeletion(firstCallback.Context, cookie.Name));

        var replayProperties = new AuthenticationProperties(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OpenIdConnectFlowState.PropertyKey] = flow,
            });
        var replay = await CreateCallbackAsync(scope.ServiceProvider, replayProperties, cookie);

        await flows.ValidateAsync(replay.Context);

        Assert.Equal("invalid_callback", replay.Context.Result?.Failure?.Message);
        Assert.False(replay.Context.HttpContext.Items.ContainsKey(
            OpenIdConnectFlowState.ValidatedKey));
    }

    [Fact]
    public async Task Wrong_binding_fails_without_consuming_the_original_flow()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var flows = scope.ServiceProvider.GetRequiredService<OpenIdConnectFlowState>();
        var beginContext = CreateHttpContext(scope.ServiceProvider);
        var properties = new AuthenticationProperties();

        await flows.BeginAsync(beginContext, properties, OidcScheme);
        var cookie = ReadCookie(beginContext);
        var wrongCookie = cookie with { Value = new string('A', 64) };

        var rejected = await CreateCallbackAsync(scope.ServiceProvider, properties, wrongCookie);
        await flows.ValidateAsync(rejected.Context);

        Assert.Equal("invalid_callback", rejected.Context.Result?.Failure?.Message);
        Assert.Equal(
            properties.Items[OpenIdConnectFlowState.PropertyKey],
            rejected.Properties.Items[OpenIdConnectFlowState.PropertyKey]);

        var accepted = await CreateCallbackAsync(scope.ServiceProvider, properties, cookie);
        await flows.ValidateAsync(accepted.Context);

        Assert.Null(accepted.Context.Result);
        Assert.True(Assert.IsType<bool>(
            accepted.Context.HttpContext.Items[OpenIdConnectFlowState.ValidatedKey]));
    }

    [Fact]
    public async Task Cancel_removes_server_flow_properties_and_binding_cookie()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var flows = scope.ServiceProvider.GetRequiredService<OpenIdConnectFlowState>();
        var context = CreateHttpContext(scope.ServiceProvider);
        var properties = new AuthenticationProperties();

        await flows.BeginAsync(context, properties, OidcScheme);
        var flow = Assert.IsType<string>(
            properties.Items[OpenIdConnectFlowState.PropertyKey]);
        var cookie = ReadCookie(context);

        await flows.CancelAsync(context, properties, OidcScheme);

        Assert.DoesNotContain(OpenIdConnectFlowState.PropertyKey, properties.Items.Keys);
        var deletion = Assert.IsType<string>(Assert.Single(
            context.Response.Headers.SetCookie,
            header => header is not null
                && header.StartsWith(cookie.Name + "=", StringComparison.Ordinal)));
        Assert.Contains(
            "expires=Thu, 01 Jan 1970 00:00:00 GMT",
            deletion,
            StringComparison.OrdinalIgnoreCase);

        var probeProperties = new AuthenticationProperties(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [OpenIdConnectFlowState.PropertyKey] = flow,
            });
        var probe = await CreateCallbackAsync(scope.ServiceProvider, probeProperties, cookie);

        await flows.ValidateAsync(probe.Context);

        Assert.Equal("invalid_callback", probe.Context.Result?.Failure?.Message);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddAuthentication()
            .AddOpenIdConnect(OidcScheme, static options =>
            {
                options.ClientId = "test-client";
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Issuer,
                    AuthorizationEndpoint = Issuer + "/authorize",
                    TokenEndpoint = Issuer + "/token",
                };
            });
        services
            .AddOpenIdConnectFlowState()
            .UseInMemory(options => options.Lifetime = TimeSpan.FromMinutes(10));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services) =>
        new()
        {
            RequestServices = services,
        };

    private static async Task<CallbackContext> CreateCallbackAsync(
        IServiceProvider services,
        AuthenticationProperties properties,
        BindingCookie cookie)
    {
        var httpContext = CreateHttpContext(services);
        httpContext.Request.Headers.Cookie = $"{cookie.Name}={cookie.Value}";
        var scheme = await services.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetSchemeAsync(OidcScheme);
        Assert.NotNull(scheme);
        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OidcScheme);
        var callbackProperties = new AuthenticationProperties(
            new Dictionary<string, string?>(properties.Items, StringComparer.Ordinal));

        return new CallbackContext(
            new MessageReceivedContext(httpContext, scheme, options, callbackProperties),
            callbackProperties);
    }

    private static BindingCookie ReadCookie(DefaultHttpContext context)
    {
        var header = Assert.IsType<string>(
            Assert.Single(context.Response.Headers.SetCookie));
        var separator = header.IndexOf(';');
        Assert.True(separator > 0);
        var pair = header[..separator];
        var equals = pair.IndexOf('=');
        Assert.True(equals > 0);

        return new BindingCookie(
            pair[..equals],
            pair[(equals + 1)..],
            header);
    }

    private static bool IsCookieDeletion(MessageReceivedContext context, string cookieName) =>
        context.Response.Headers.SetCookie.Any(
            header => header is not null
                && header.StartsWith(cookieName + "=", StringComparison.Ordinal)
                && header.Contains(
                    "expires=Thu, 01 Jan 1970 00:00:00 GMT",
                    StringComparison.OrdinalIgnoreCase));

    private sealed record BindingCookie(string Name, string Value, string Header);

    private sealed record CallbackContext(
        MessageReceivedContext Context,
        AuthenticationProperties Properties);
}
