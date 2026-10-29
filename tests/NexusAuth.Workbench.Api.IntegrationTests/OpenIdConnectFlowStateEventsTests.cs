using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NexusAuth.Extension;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class OpenIdConnectFlowStateEventsTests
{
    private const string Scheme = "standalone-client";

    [Fact]
    public async Task Scoped_events_validate_browser_binding_once_without_workbench_services()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        using var otherScope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var events = services.GetRequiredService<OpenIdConnectFlowStateEvents>();
        var callbackCount = 0;
        events.OnMessageReceived = _ =>
        {
            callbackCount++;
            return Task.CompletedTask;
        };
        Assert.Same(events, services.GetRequiredService<OpenIdConnectFlowStateEvents>());
        Assert.NotSame(events, otherScope.ServiceProvider.GetRequiredService<OpenIdConnectFlowStateEvents>());
        var flows = services.GetRequiredService<OpenIdConnectFlowState>();
        var properties = new AuthenticationProperties();
        var challenge = new DefaultHttpContext { RequestServices = services };
        await flows.BeginAsync(challenge, properties, Scheme);
        var cookie = Assert.IsType<string>(Assert.Single(challenge.Response.Headers.SetCookie)).Split(';')[0];
        var replay = new AuthenticationProperties(new Dictionary<string, string?>(properties.Items));
        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Scheme);

        MessageReceivedContext Callback(AuthenticationProperties state)
        {
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.Headers.Cookie = cookie;
            return new MessageReceivedContext(http, CreateScheme(), options, state);
        }

        var accepted = Callback(properties);
        await events.MessageReceived(accepted);
        Assert.Null(accepted.Result);
        Assert.True(accepted.HttpContext.Items.ContainsKey(OpenIdConnectFlowState.ValidatedKey));
        var rejected = Callback(replay);
        await events.MessageReceived(rejected);
        Assert.Equal("invalid_callback", rejected.Result?.Failure?.Message);
        Assert.Equal(1, callbackCount);
    }

    [Theory]
    [InlineData("https://provider.test/authorize", true, true)]
    [InlineData("http://localhost/authorize", false, true)]
    [InlineData("http://provider.test/authorize", true, false)]
    [InlineData("https://user:password@provider.test/authorize", true, false)]
    [InlineData("https://provider.test/authorize#fragment", true, false)]
    [InlineData("/authorize", true, false)]
    public async Task Both_redirect_events_validate_endpoints_without_setting_application_redirects(
        string endpoint, bool requireHttps, bool accepted)
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<OpenIdConnectFlowStateEvents>();
        var options = new OpenIdConnectOptions { RequireHttpsMetadata = requireHttps };
        var context = new RedirectContext(new DefaultHttpContext(), CreateScheme(), options, new AuthenticationProperties())
        {
            ProtocolMessage = new OpenIdConnectMessage
            {
                IssuerAddress = endpoint,
                RedirectUri = "https://client.test/custom-callback",
                PostLogoutRedirectUri = "https://client.test/custom-logout",
            },
        };

        if (accepted)
        {
            await events.RedirectToIdentityProvider(context);
            await events.RedirectToIdentityProviderForSignOut(context);
            Assert.Equal("https://client.test/custom-callback", context.ProtocolMessage.RedirectUri);
            Assert.Equal("https://client.test/custom-logout", context.ProtocolMessage.PostLogoutRedirectUri);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => events.RedirectToIdentityProvider(context));
            await Assert.ThrowsAsync<InvalidOperationException>(() => events.RedirectToIdentityProviderForSignOut(context));
        }
    }

    private static AuthenticationScheme CreateScheme() => new(Scheme, null, typeof(OpenIdConnectHandler));

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddOpenIdConnect(Scheme, options =>
        {
            options.ClientId = "sdk-client";
            options.EventsType = typeof(OpenIdConnectFlowStateEvents);
            options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://provider.test" };
        });
        services.AddOpenIdConnectFlowState().UseInMemory();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
