using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api;

/// <summary>Configures Workbench as a single Cookie + OpenID Connect BFF client.</summary>
public static class WorkbenchAuthenticationExtensions
{
    public static IServiceCollection AddWorkbenchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WorkbenchAuthenticationOptions>()
            .Bind(configuration.GetSection(WorkbenchAuthenticationOptions.SectionName))
            .PostConfigure(options => options.Normalize())
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<WorkbenchAuthenticationOptions>, WorkbenchAuthenticationOptionsValidator>());
        services.AddOpenIdConnectFlowState().UseConfiguration(configuration.GetSection("Auth:FlowState"));

        services.AddScoped<WorkbenchCookieAuthenticationEvents>();
        services.AddScoped<WorkbenchOpenIdConnectEvents>();
        services.AddScoped<IWorkbenchTokenClient, WorkbenchTokenClient>();
        services.AddSingleton<IWorkbenchTokenRefreshCoordinator, WorkbenchTokenRefreshCoordinator>();
        services.AddTransient<WorkbenchBasicAuthenticationHandler>();
        services.AddHttpClient(WorkbenchTokenClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
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
                options.EventsType = typeof(WorkbenchOpenIdConnectEvents));
        services.AddOptions<CookieAuthenticationOptions>(WorkbenchAuthenticationDefaults.CookieScheme)
            .Configure<IOptions<WorkbenchSecurityOptions>>((options, security) =>
                options.Cookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value));
        services.AddOptions<OpenIdConnectOptions>(WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
            .Configure<IOptions<WorkbenchAuthenticationOptions>, IHttpClientFactory,
                IOptions<WorkbenchSecurityOptions>, IOptions<OpenIdConnectFlowStateOptions>>(
                (options, auth, http, security, flow) =>
                {
                    var settings = auth.Value;
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
                    options.Backchannel = http.CreateClient(WorkbenchTokenClient.HttpClientName);
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.NonceCookie.SameSite = SameSiteMode.Lax;
                    options.CorrelationCookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value);
                    options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy;
                    options.RemoteAuthenticationTimeout = flow.Value.Lifetime;
                });
        services.AddHostedService<WorkbenchDiscoveryWarmupService>();
        return services;
    }
}
