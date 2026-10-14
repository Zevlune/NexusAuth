using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NexusAuth.Extension;
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

    private sealed class Harness : IDisposable
    {
        private readonly string subject = Guid.NewGuid().ToString();
        private readonly string refreshToken = Guid.NewGuid().ToString();
        private readonly ServiceProvider services;
        public bool Active { get; set; } = true;
        public string? Failure { get; init; }
        public int SignOutCount { get; private set; }
        public int RefreshCount { get; private set; }
        public WorkbenchCookieAuthenticationEvents Events { get; }

        public Harness()
        {
            var oidc = RecordingDispatchProxy.Create<IOidcWorkbenchService>((method, _) => method.Name switch
            {
                "get_ClientId" => "workbench-client",
                nameof(IOidcWorkbenchService.RefreshTokensAsync) => Refresh(),
                nameof(IOidcWorkbenchService.IntrospectAccessTokenAsync) => Introspect(),
                _ => throw new NotSupportedException(method.Name),
            });
            Events = new WorkbenchCookieAuthenticationEvents(oidc);
            var authentication = RecordingDispatchProxy.Create<IAuthenticationService>((method, _) =>
            {
                if (method.Name == nameof(IAuthenticationService.SignOutAsync)) SignOutCount++;
                return null;
            });
            services = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider();
        }

        private WorkbenchTokenResult Refresh()
        {
            RefreshCount++;
            return new WorkbenchTokenResult("new-access", "new-refresh", "new-id", 300);
        }

        private AccessTokenIntrospectionResult Introspect()
        {
            if (Failure == "exception") throw new HttpRequestException("Provider unavailable.");
            return new AccessTokenIntrospectionResult(Active,
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
