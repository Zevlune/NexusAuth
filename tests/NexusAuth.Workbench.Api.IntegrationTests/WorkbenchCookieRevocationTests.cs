using FrameworkOptions = Microsoft.Extensions.Options.Options;
using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Authentication.Events;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api.Options;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NexusAuth.Workbench.Api;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchCookieRevocationTests
{
    [Fact]
    public async Task Unexpired_cookie_is_rejected_and_signed_out_when_its_token_is_revoked()
    {
        using var harness = new Harness { Active = false };
        var context = harness.Context(TimeSpan.FromMinutes(10));

        await harness.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.Equal(1, harness.SignOutCount);
        Assert.Equal(0, harness.RefreshCount);
    }

    [Fact]
    public async Task Active_unexpired_cookie_remains_usable()
    {
        using var harness = new Harness();
        var context = harness.Context(TimeSpan.FromMinutes(10));

        await harness.Events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);
        Assert.Equal(0, harness.SignOutCount);
        Assert.Equal(0, harness.RefreshCount);
    }

    [Fact]
    public async Task Cookie_does_not_reuse_a_cached_refresh_after_administrator_revocation()
    {
        using var harness = new Harness();
        var first = harness.Context(TimeSpan.FromSeconds(10));
        await harness.Events.ValidatePrincipal(first);
        Assert.True(first.ShouldRenew);
        Assert.Equal("new-access", first.Properties.GetTokenValue("access_token"));

        harness.Active = false;
        var concurrentRequest = harness.Context(TimeSpan.FromSeconds(10));
        await harness.Events.ValidatePrincipal(concurrentRequest);

        Assert.Null(concurrentRequest.Principal);
        Assert.Equal(1, harness.SignOutCount);
        Assert.Equal(1, harness.RefreshCount); // Same completed single-flight result, revalidated.
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("client")]
    [InlineData("exception")]
    public async Task Invalid_introspection_cannot_renew_a_cookie(string failure)
    {
        using var harness = new Harness { Failure = failure };
        var context = harness.Context(TimeSpan.FromSeconds(10));

        await harness.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
        Assert.False(context.ShouldRenew);
        Assert.Equal(1, harness.SignOutCount);
    }

    [Fact]
    public async Task Refresh_without_new_refresh_or_id_token_preserves_the_existing_tokens()
    {
        using var harness = new Harness { OmitOptionalTokens = true };
        var context = harness.Context(TimeSpan.FromSeconds(10));
        var previousRefresh = context.Properties.GetTokenValue("refresh_token");
        await harness.Events.ValidatePrincipal(context);
        Assert.True(context.ShouldRenew);
        Assert.Equal("new-access", context.Properties.GetTokenValue("access_token"));
        Assert.Equal(previousRefresh, context.Properties.GetTokenValue("refresh_token"));
        Assert.Equal("old-id", context.Properties.GetTokenValue("id_token"));
    }

    private sealed class Harness : IDisposable
    {
        private readonly string subject = Guid.NewGuid().ToString();
        private readonly string refreshToken = Guid.NewGuid().ToString();
        private readonly ServiceProvider services;
        public bool Active { get; set; } = true;
        public bool OmitOptionalTokens { get; init; }
        public string? Failure { get; init; }
        public int SignOutCount { get; private set; }
        public int RefreshCount { get; private set; }
        public NexusAuthCookieAuthenticationEvents Events { get; }

        public Harness()
        {
            var oidc = RecordingDispatchProxy.Create<INexusAuthTokenClient>((method, _) => method.Name switch
            {
                "get_ClientId" => "workbench-client",
                nameof(INexusAuthTokenClient.RefreshTokensAsync) => Refresh(),
                nameof(INexusAuthTokenClient.IntrospectAccessTokenAsync) => Introspect(),
                _ => throw new NotSupportedException(method.Name),
            });
            var authentication = RecordingDispatchProxy.Create<IAuthenticationService>((method, _) =>
            {
                if (method.Name == nameof(IAuthenticationService.SignOutAsync)) SignOutCount++;
                return null;
            });
            services = new ServiceCollection()
                .AddSingleton(authentication)
                .AddSingleton(oidc)
                .AddSingleton<INexusAuthTokenRefreshCoordinator, NexusAuthTokenRefreshCoordinator>()
                .BuildServiceProvider();
            Events = new NexusAuthCookieAuthenticationEvents(oidc,
                FrameworkOptions.Create(new NexusAuthClientOptions
                {
                    Authority = "https://issuer.test",
                    Audience = "workbench.api",
                    ClientId = "workbench-client",
                    ClientSecret = "secret",
                    RedirectUri = "https://app.test/signin-oidc",
                    PostLogoutRedirectUri = "https://app.test/",
                    Scope = "openid offline_access",
                    SignOutProvider = true,
                    RequireHttpsMetadata = true
                }),
                services.GetRequiredService<INexusAuthTokenRefreshCoordinator>());
        }

        private NexusAuthTokenResult Refresh()
        {
            RefreshCount++;
            return new NexusAuthTokenResult("new-access", OmitOptionalTokens ? null : "new-refresh",
                OmitOptionalTokens ? null : "new-id", 300);
        }

        private NexusAuthIntrospectionResult Introspect()
        {
            if (Failure == "exception") throw new HttpRequestException("Provider unavailable.");
            return new NexusAuthIntrospectionResult(Active,
                Failure == "subject" ? "different-user" : subject,
                Failure == "client" ? "different-client" : "workbench-client");
        }

        public CookieValidatePrincipalContext Context(TimeSpan remaining)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "cookie"));
            var properties = new AuthenticationProperties();
            properties.StoreTokens([
                new AuthenticationToken { Name = "access_token", Value = "old-access" },
                new AuthenticationToken { Name = "refresh_token", Value = refreshToken },
                new AuthenticationToken { Name = "id_token", Value = "old-id" },
                new AuthenticationToken { Name = "expires_at", Value = DateTimeOffset.UtcNow.Add(remaining).ToString("O", CultureInfo.InvariantCulture) },
            ]);
            var scheme = new AuthenticationScheme(WorkbenchAuthenticationDefaults.CookieScheme, null, typeof(CookieAuthenticationHandler));
            return new CookieValidatePrincipalContext(new DefaultHttpContext { RequestServices = services }, scheme,
                new CookieAuthenticationOptions(), new AuthenticationTicket(principal, properties, scheme.Name));
        }

        public void Dispose() => services.Dispose();
    }
}
