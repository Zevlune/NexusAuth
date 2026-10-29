using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api;

/// <summary>Configures Workbench as a single Cookie + OpenID Connect BFF client.</summary>
public static class WorkbenchAuthenticationExtensions
{
    public static IServiceCollection AddWorkbenchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = WorkbenchAuthenticationSettings.FromConfiguration(configuration);
        services.AddSingleton(settings);
        ConfigureFlowStorage(services, configuration);
        services.AddScoped<WorkbenchCookieAuthenticationEvents>();
        services.AddScoped<WorkbenchOpenIdConnectEvents>();
        services.AddScoped<IWorkbenchOidcClient, WorkbenchOidcClient>();
        services.AddSingleton<IWorkbenchTokenRefreshCoordinator, WorkbenchTokenRefreshCoordinator>();
        services.AddTransient<WorkbenchBasicAuthenticationHandler>();
        services.AddHttpClient(WorkbenchOidcClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler<WorkbenchBasicAuthenticationHandler>();

        services.AddAuthentication(WorkbenchAuthenticationDefaults.CookieScheme)
            .AddCookie(WorkbenchAuthenticationDefaults.CookieScheme, options =>
            {
                options.Cookie.Name = ".NexusAuth.Workbench";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/api/auth/login";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(24);
                options.EventsType = typeof(WorkbenchCookieAuthenticationEvents);
            })
            .AddOpenIdConnect(WorkbenchAuthenticationDefaults.OpenIdConnectScheme, options =>
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
                options.DisableTelemetry = true;
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                options.Scope.Clear();
                foreach (var scope in settings.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    options.Scope.Add(scope);
                options.TokenValidationParameters.ValidIssuer = settings.Authority;
                options.TokenValidationParameters.ValidAudience = settings.ClientId;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                options.TokenValidationParameters.NameClaimType = "name";
                options.EventsType = typeof(WorkbenchOpenIdConnectEvents);
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
            });
        services.AddOptions<CookieAuthenticationOptions>(WorkbenchAuthenticationDefaults.CookieScheme)
            .Configure<IOptions<WorkbenchSecurityOptions>>((options, security) =>
                options.Cookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value));
        services.AddOptions<OpenIdConnectOptions>(WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
            .Configure<IHttpClientFactory, IOptions<WorkbenchSecurityOptions>, IOptions<OpenIdConnectFlowStateOptions>>(
                (options, http, security, flow) =>
                {
                    options.Backchannel = http.CreateClient(WorkbenchOidcClient.HttpClientName);
                    options.CorrelationCookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value);
                    options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy;
                    options.RemoteAuthenticationTimeout = flow.Value.Lifetime;
                });
        // Reuse the manager constructed by the framework; no second discovery/cache implementation.
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(provider =>
            provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(WorkbenchAuthenticationDefaults.OpenIdConnectScheme).ConfigurationManager!);
        services.AddHostedService<WorkbenchDiscoveryWarmupService>();
        return services;
    }

    private static void ConfigureFlowStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenIdConnectFlowStateOptions>()
            .Bind(configuration.GetSection("Auth:FlowState"))
            .Validate(options => options.Capacity is >= 1 and <= 100_000
                && options.Lifetime > TimeSpan.Zero && options.Lifetime <= TimeSpan.FromHours(1),
                "Auth:FlowState capacity must be 1..100000 and lifetime must be positive and at most one hour.")
            .ValidateOnStart();
        var flow = services.AddOpenIdConnectFlowState();
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
    }
}
