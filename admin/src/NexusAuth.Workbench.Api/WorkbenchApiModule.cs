using Luck.AppModule;
using Luck.AutoDependencyInjection;
using Luck.Framework.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Application.Services.Email;
using NexusAuth.Application.Services.Tokens;
using NexusAuth.Extension;
using NexusAuth.Persistence;
using NexusAuth.Workbench.Api.Authorization;

namespace NexusAuth.Workbench.Api;

[DependsOn(
    typeof(AutoDependencyAppModule),
    typeof(EntityFrameworkCoreModule)
)]
public class WorkbenchApiModule : LuckAppModule
{
    public override void ConfigureServices(ConfigureServicesContext context)
    {
        var services = context.Services;
        var configuration = services.GetConfiguration();
        var settings = WorkbenchAuthenticationSettings.FromConfiguration(configuration);

        services.AddNexusAuthEmail(configuration);
        ConfigureSecurity(services, configuration);
        ConfigureAuthentication(services, settings);
        services.AddWorkbenchAuthorization();
        ConfigureBootstrap(services, configuration);

        base.ConfigureServices(context);
    }

    private static void ConfigureAuthentication(IServiceCollection services, WorkbenchAuthenticationSettings settings)
    {
        var authentication = services.AddAuthentication(settings.GatewayEnabled
            ? WorkbenchAuthenticationDefaults.BearerScheme
            : WorkbenchAuthenticationDefaults.Scheme);

        if (!settings.GatewayEnabled)
        {
            ConfigureOidcClient(services, settings);
            ConfigureCookieAuthentication(authentication);
        }

        ConfigureBearerAuthentication(authentication, settings);
    }

    private static void ConfigureOidcClient(IServiceCollection services, WorkbenchAuthenticationSettings settings)
    {
        services.AddNexusAuth(options =>
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.RedirectUri = settings.RedirectUri;
            options.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
            options.Scope = settings.Scope;
            options.SignOutProvider = settings.SignOutProvider;
            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        });
    }

    private static void ConfigureCookieAuthentication(AuthenticationBuilder authentication)
    {
        authentication.AddPolicyScheme(WorkbenchAuthenticationDefaults.Scheme, "Cookie or Bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? WorkbenchAuthenticationDefaults.BearerScheme
                    : WorkbenchAuthenticationDefaults.CookieScheme;
        });
        authentication.AddCookie(WorkbenchAuthenticationDefaults.CookieScheme, options =>
        {
            options.Cookie.Name = ".NexusAuth.Workbench";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/api/auth/login";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(24);
            options.EventsType = typeof(IWorkbenchCookieAuthenticationEvents);
        });
        authentication.Services.AddOptions<CookieAuthenticationOptions>(WorkbenchAuthenticationDefaults.CookieScheme)
            .Configure<IOptions<WorkbenchSecurityOptions>>((options, security) =>
                options.Cookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value));
    }

    private static void ConfigureBearerAuthentication(AuthenticationBuilder authentication, WorkbenchAuthenticationSettings settings)
    {
        authentication.AddJwtBearer(WorkbenchAuthenticationDefaults.BearerScheme, options =>
        {
            options.Authority = settings.Authority;
            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
            options.MetadataAddress = $"{settings.Authority}/.well-known/openid-configuration";
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = true,
                NameClaimType = "name",
                RoleClaimType = "role",
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context => ValidateBearerTokenAsync(context, settings.AllowedBearerClientIds),
            };
        });
    }

    private static async Task ValidateBearerTokenAsync(TokenValidatedContext context, IReadOnlySet<string> allowedClientIds)
    {
        if (!string.Equals(context.Principal?.FindFirst("token_use")?.Value, "access_token", StringComparison.Ordinal))
        {
            context.Fail("Only access_token is accepted.");
            return;
        }

        var tokenClientId = context.Principal?.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(tokenClientId) || !allowedClientIds.Contains(tokenClientId))
        {
            context.Fail("The token was issued to an unauthorized client.");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IUserTokenValidator>();
        if (context.Principal is null || !await validator.IsActiveAsync(context.Principal, context.HttpContext.RequestAborted))
            context.Fail("The token has been revoked or the user is inactive.");
    }

    private static void ConfigureBootstrap(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkbenchBootstrapOptions>(configuration.GetSection(WorkbenchBootstrapOptions.SectionName));
        services.AddHostedService<WorkbenchClientCredentialHostedService>();
    }

    private static void ConfigureSecurity(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WorkbenchSecurityOptions>()
            .Bind(configuration.GetSection(WorkbenchSecurityOptions.SectionName))
            .Validate(
                options => WorkbenchSecurityOptions.TryParseCookieSecurePolicy(options.CookieSecurePolicy, out _),
                "Security:CookieSecurePolicy must be SameAsRequest or Always.")
            .ValidateOnStart();
        services.Configure<ForwardedHeadersOptions>(
            options => WorkbenchSecurityOptions.ConfigureForwardedHeaders(options, configuration));
    }

    public override void ApplicationInitialization(ApplicationContext context)
    {
        var app = context.GetApplicationBuilder();
        app.UseAuthentication();
        app.UseAuthorization();

        base.ApplicationInitialization(context);
    }
}
