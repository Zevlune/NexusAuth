using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;
using NexusAuth.Shared.OAuth;
using NexusAuth.Workbench.Api;
using NexusAuth.Workbench.Api.Authentication;
using NexusAuth.Workbench.Api.Authentication.Events;
using NexusAuth.Workbench.Api.Authentication.Tokens;
using Xunit;

namespace NexusAuth.Workbench.Api.IntegrationTests;

public sealed class WorkbenchBffAuthenticationTests
{
    private const string Authority = "https://issuer.example.test";
    private const string Audience = "nexusauth.workbench.api";
    private const string ClientId = "nexusauth.workbench";
    private const string WorkbenchScope = OidcScopes.OpenId + " " + OidcScopes.Profile
        + " " + OidcScopes.OfflineAccess;

    [Fact]
    public async Task Workbench_registers_cookie_oidc_and_bearer_with_a_request_selector()
    {
        using var factory = CreateFactory();
        var schemes = (await factory.Services
                .GetRequiredService<IAuthenticationSchemeProvider>()
                .GetAllSchemesAsync())
            .ToArray();
        var defaults = factory.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

        Assert.Contains(schemes, scheme =>
            scheme.Name == WorkbenchAuthenticationDefaults.CookieScheme
            && scheme.HandlerType == typeof(CookieAuthenticationHandler));
        Assert.Contains(schemes, scheme =>
            scheme.Name == WorkbenchAuthenticationDefaults.OpenIdConnectScheme
            && scheme.HandlerType == typeof(OpenIdConnectHandler));
        Assert.Contains(schemes, scheme => scheme.Name == WorkbenchAuthenticationDefaults.BearerScheme
            && scheme.HandlerType == typeof(JwtBearerHandler));
        Assert.Equal(WorkbenchAuthenticationDefaults.Scheme, defaults.DefaultScheme);

        var bearer = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(WorkbenchAuthenticationDefaults.BearerScheme);
        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
        Assert.Same(oidc.ConfigurationManager, bearer.ConfigurationManager);
        Assert.Equal(Audience, bearer.TokenValidationParameters.ValidAudience);
        Assert.Equal(typeof(WorkbenchJwtBearerEvents), bearer.EventsType);

        var cookie = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(WorkbenchAuthenticationDefaults.CookieScheme);
        Assert.Equal(typeof(WorkbenchCookieAuthenticationEvents), cookie.EventsType);

        using var scope = factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IWorkbenchTokenClient>());
        Assert.NotNull(scope.ServiceProvider.GetService<IFlowStateStore>());
        Assert.NotNull(scope.ServiceProvider.GetService<IOptions<OpenIdConnectFlowStateOptions>>());
    }

    [Fact]
    public void Oidc_client_registration_is_scoped()
    {
        using var factory = CreateFactory();
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>();
        var sameScope = firstScope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>();
        var otherScope = secondScope.ServiceProvider.GetRequiredService<IWorkbenchTokenClient>();

        Assert.Same(first, sameScope);
        Assert.NotSame(first, otherScope);
    }

    [Theory]
    [InlineData("Auth:Authority")]
    [InlineData("Auth:Audience")]
    [InlineData("Auth:ClientId")]
    [InlineData("Auth:ClientSecret")]
    [InlineData("Auth:RedirectUri")]
    [InlineData("Auth:PostLogoutRedirectUri")]
    [InlineData("Auth:Scope")]
    public void Bff_fails_startup_for_empty_required_authentication_configuration(
        string key)
    {
        using var factory = CreateFactory(builder => builder.UseSetting(key, ""));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(key, exception.ToString());
    }

    [Theory]
    [InlineData("relative-provider")]
    [InlineData("ftp://issuer.example.test")]
    [InlineData("https://user@issuer.example.test")]
    [InlineData("https://issuer.example.test?tenant=test")]
    [InlineData("https://issuer.example.test#fragment")]
    public void Invalid_authority_fails_startup(string authority)
    {
        using var factory = CreateFactory(builder => builder.UseSetting("Auth:Authority", authority));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth:Authority", exception.ToString());
        Assert.Contains("absolute HTTP(S) URI", exception.ToString());
    }

    [Fact]
    public void Oidc_options_keep_security_defaults_and_append_the_resource_scope()
    {
        using var factory = CreateFactory(builder => builder
            .UseSetting("Auth:Authority", Authority + "/")
            .UseSetting("Auth:Scope", WorkbenchScope));
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
        var validation = options.TokenValidationParameters;

        Assert.True(options.RequireHttpsMetadata);
        Assert.Equal(Authority, options.Authority);
        Assert.Equal(Authority + "/.well-known/openid-configuration", options.MetadataAddress);
        Assert.Equal(OpenIdConnectResponseType.Code, options.ResponseType);
        Assert.True(options.UsePkce);
        Assert.True(options.SaveTokens);
        Assert.False(options.MapInboundClaims);
        Assert.Equal(
            $"{WorkbenchScope} {Audience}",
            string.Join(" ", options.Scope));
        Assert.True(validation.ValidateIssuer);
        Assert.Equal(Authority, validation.ValidIssuer);
        Assert.True(validation.ValidateAudience);
        Assert.Equal(ClientId, validation.ValidAudience);
        Assert.True(validation.RequireSignedTokens);
        Assert.True(validation.ValidateLifetime);
        Assert.True(validation.RequireExpirationTime);
        Assert.Equal([SecurityAlgorithms.RsaSha256], validation.ValidAlgorithms);
        Assert.Equal(TimeSpan.FromSeconds(30), validation.ClockSkew);
        Assert.Equal("name", validation.NameClaimType);
        Assert.Equal(ClaimTypes.Role, validation.RoleClaimType);

        var cookie = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(WorkbenchAuthenticationDefaults.CookieScheme);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
    }

    [Fact]
    public void Http_authority_is_rejected_while_https_metadata_validation_is_enabled_by_default()
    {
        using var factory = CreateFactory(builder =>
            builder.UseSetting("Auth:Authority", "http://localhost:5100"));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth:Authority", exception.ToString());
        Assert.Contains("must use HTTPS", exception.ToString());
    }

    [Fact]
    public async Task Anonymous_requests_and_an_invalid_bearer_token_are_unauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0In0.signature");
        using var bearer = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        using var protectedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        protectedRequest.Headers.Authorization = request.Headers.Authorization;
        using var challenge = await client.SendAsync(protectedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains(challenge.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    private static WebApplicationFactory<WorkbenchApiModule> CreateFactory(
        Action<IWebHostBuilder>? configure = null)
    {
        return new WebApplicationFactory<WorkbenchApiModule>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing")
                .UseSetting("Auth:Authority", Authority)
                .UseSetting("Auth:Audience", Audience)
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:ClientSecret", "test-only-secret")
                .UseSetting("Auth:RedirectUri", "https://dashboard.example.test/signin-oidc")
                .UseSetting("Auth:PostLogoutRedirectUri", "https://dashboard.example.test/")
                .UseSetting("Auth:Scope", WorkbenchScope);
            configure?.Invoke(builder);
        });
    }
}
