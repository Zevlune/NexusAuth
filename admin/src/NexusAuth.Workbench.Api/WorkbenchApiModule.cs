using Luck.AppModule;
using Luck.AutoDependencyInjection;
using Luck.Framework.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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
            ConfigureOpenIdConnect(authentication, settings);
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
            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        });
        services.AddSingleton(settings);
        var flow = services.AddWorkbenchFlowState();
        var configuration = services.GetConfiguration();
        services.AddOptions<WorkbenchFlowStateOptions>()
            .Bind(configuration.GetSection("Auth:FlowState"))
            .Validate(options => options.Capacity is >= 1 and <= 100_000
                && options.Lifetime > TimeSpan.Zero && options.Lifetime <= TimeSpan.FromHours(1),
                "Auth:FlowState capacity must be 1..100000 and lifetime must be positive and at most one hour.")
            .ValidateOnStart();
        switch (configuration["Auth:FlowState:Provider"] ?? "InMemory")
        {
            case "InMemory": flow.UseInMemory(); break;
            case "Redis":
                flow.UseRedis(options => configuration.GetSection("Auth:FlowState:Redis").Bind(options));
                services.AddOptions<RedisFlowStateOptions>()
                    .Validate(options => !string.IsNullOrWhiteSpace(options.KeyPrefix)
                        && options.KeyPrefix.IndexOfAny(['{', '}']) < 0 && options.Database >= -1,
                        "Auth:FlowState:Redis key prefix or database is invalid.")
                    .ValidateOnStart();
                break;
            default: throw new InvalidOperationException("Auth:FlowState:Provider must be InMemory or Redis.");
        }
        services.AddScoped<WorkbenchOpenIdConnectEvents>();
        services.AddSingleton<IWorkbenchTokenRefreshCoordinator, WorkbenchTokenRefreshCoordinator>();
        services.AddHostedService<WorkbenchDiscoveryWarmupService>();
    }

    private static void ConfigureOpenIdConnect(AuthenticationBuilder authentication, WorkbenchAuthenticationSettings settings)
    {
        authentication.AddOpenIdConnect(WorkbenchAuthenticationDefaults.OpenIdConnectScheme, options =>
        {
            options.SignInScheme = WorkbenchAuthenticationDefaults.CookieScheme;
            options.SignOutScheme = WorkbenchAuthenticationDefaults.CookieScheme;
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
            options.CallbackPath = new Uri(settings.RedirectUri).AbsolutePath;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.SaveTokens = true;
            options.MapInboundClaims = false;
            options.UseTokenLifetime = false;
            options.DisableTelemetry = true;
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            options.Scope.Clear();
            foreach (var scope in settings.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                options.Scope.Add(scope);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = settings.Authority,
                ValidateAudience = true, ValidAudience = settings.ClientId,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "name", RoleClaimType = "role",
            };
            options.EventsType = typeof(WorkbenchOpenIdConnectEvents);
            // Query callbacks work with Lax cookies and do not require cross-site POST cookies.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
        });
        authentication.Services.AddOptions<OpenIdConnectOptions>(WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
            .Configure<IConfigurationManager<OpenIdConnectConfiguration>, IOptions<WorkbenchSecurityOptions>, IOptions<WorkbenchFlowStateOptions>>(
                (options, metadata, security, flow) =>
                {
                    options.ConfigurationManager = metadata;
                    options.CorrelationCookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value);
                    options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy;
                    options.RemoteAuthenticationTimeout = flow.Value.Lifetime;
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

    private static async Task ValidateBearerTokenAsync(Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext context, IReadOnlySet<string> allowedClientIds)
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
