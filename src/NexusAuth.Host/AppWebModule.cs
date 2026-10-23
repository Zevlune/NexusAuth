using NexusAuth.Application.Clients.Authentication;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Host.TokenEndpoint;
using Luck.AppModule;
using Luck.AutoDependencyInjection;
using Luck.Framework.Infrastructure;
using NexusAuth.Persistence;
using NexusAuth.Host.Authentication;
using Microsoft.Extensions.Options;
using NexusAuth.Application.Users;
using System.Threading.RateLimiting;
using Fido2NetLib;
using NexusAuth.Application.Services.Email;
using NexusAuth.Application.Services.ExternalIdentity;

namespace NexusAuth.Host;

[DependsOn(
    typeof(AutoDependencyAppModule),
    typeof(EntityFrameworkCoreModule)
)]
public class AppWebModule : LuckAppModule
{
    public const string AuthenticationScheme = "NexusAuth.Identity";

    /// <summary>
    /// 注册 Host 层所需的配置与基础认证服务。
    /// </summary>
    public override void ConfigureServices(ConfigureServicesContext context)
    {
        var services = context.Services;
        var configuration = services.GetConfiguration();

        services.AddScopeKeyedServices(typeof(IClientAuthenticationStrategy).Assembly);
        services.AddTokenGrantStrategies();
        services.AddNexusAuthTokenSigning(configuration);
        services.AddNexusAuthEmail(configuration);
        // The upstream URL contains AppSecret and the one-time code. Suppress
        // HttpClient URL logging and redirects for this credential-bearing call.
        services.AddHttpClient(WeChatMiniProgramLoginService.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.Configure<NexusAuthSecurityOptions>(configuration.GetSection("Security"));
        services.AddOptions<LoginFlowOptions>()
            .Bind(configuration.GetSection(LoginFlowOptions.SectionName))
            .PostConfigure(options => options.ApplyDefaults())
            .ValidateOnStart();
        services.AddOptions<WebAuthnOptions>()
            .Bind(configuration.GetSection(WebAuthnOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<LoginPageOptions>()
            .Bind(configuration.GetSection(LoginPageOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SelfRegistrationOptions>()
            .Bind(configuration.GetSection(SelfRegistrationOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<WeChatMiniProgramOptions>()
            .Bind(configuration.GetSection(WeChatMiniProgramOptions.SectionName))
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ClientId),
                "WeChat:MiniProgram:ClientId is required when mini program login is enabled.")
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AppId),
                "WeChat:MiniProgram:AppId is required when mini program login is enabled.")
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.AppSecret),
                "WeChat:MiniProgram:AppSecret is required when mini program login is enabled.")
            .Validate(options => !options.Enabled || (Uri.TryCreate(options.Code2SessionEndpoint, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo)),
                "WeChat:MiniProgram:Code2SessionEndpoint must be an HTTPS URL without query, fragment or user info.")
            .ValidateOnStart();
        services.AddSingleton<IFido2>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<WebAuthnOptions>>().Value;
            return new Fido2(new Fido2Configuration
            {
                ServerDomain = options.RelyingPartyId,
                ServerName = options.RelyingPartyName,
                Origins = options.Origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
            });
        });
        services.AddOptions<SliderCaptchaOptions>()
            .Bind(configuration.GetSection(SliderCaptchaOptions.SectionName))
            .ValidateOnStart();
        services.AddMemoryCache();
        services.AddOptions<TotpOptions>()
            .Bind(configuration.GetSection(TotpOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "Totp:Issuer is required.")
            .Validate(
                options => options.EnrollmentLifetimeMinutes is >= 1 and <= 30,
                "Totp:EnrollmentLifetimeMinutes must be between 1 and 30.")
            .ValidateOnStart();
        services.Configure<BootstrapAdminOptions>(configuration.GetSection("BootstrapAdmin"));
        services.AddHostedService<BootstrapAdminHostedService>();

        // Keep abuse controls at the host boundary so every sensitive
        // endpoint, including Razor login/device pages, shares one policy.
        // The limiter is partitioned by endpoint family and source address;
        // account lockout is handled separately in the application layer.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(CreateRateLimitPartition);
            options.OnRejected = static async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = "60";
                response.ContentType = "application/json";

                await response.WriteAsJsonAsync(
                    new
                    {
                        error = "too_many_requests",
                        error_description = "Too many requests. Please retry later."
                    },
                    cancellationToken);
            };
        });

        // Cookie Authentication for SSO login session
        services.AddAuthentication(AuthenticationScheme)
            .AddCookie(AuthenticationScheme, options =>
            {
                options.Cookie.Name = ".NexusAuth.Identity";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/account/login";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.EventsType = typeof(ISsoCookieAuthenticationEvents);
            });
        services.AddAuthorization();

        base.ConfigureServices(context);
    }

    /// <summary>
    /// 配置应用初始化中间件管道。
    /// </summary>
    public override void ApplicationInitialization(ApplicationContext context)
    {
        var app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        base.ApplicationInitialization(context);
    }

    private static RateLimitPartition<string> CreateRateLimitPartition(HttpContext context)
    {
        var path = context.Request.Path;
        var (family, tokenLimit) = GetRateLimitFamily(path);
        if (family is null)
            return RateLimitPartition.GetNoLimiter("unlimited");

        var address = ClientIpConfiguration.GetAddress(context);
        if (string.IsNullOrWhiteSpace(address))
            address = "unknown";

        var partitionKey = $"{family}:{address}";
        return RateLimitPartition.GetTokenBucketLimiter(
            partitionKey,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = tokenLimit,
                TokensPerPeriod = tokenLimit,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    }

    private static (string? Family, int TokenLimit) GetRateLimitFamily(PathString path)
    {
        if (path.StartsWithSegments("/account/forgot-password") || path.StartsWithSegments("/account/email"))
            return ("email", 10);
        if (path.StartsWithSegments("/account/login"))
            return ("login", 10);

        if (path.StartsWithSegments("/account/passkey")
            || path.StartsWithSegments("/account/passkeys")
            || path.StartsWithSegments("/account/PasskeyEnrollment"))
            return ("passkey", 10);

        if (path.StartsWithSegments("/account/register"))
            return ("registration", 5);

        if (path.StartsWithSegments("/connect/token"))
            return ("token", 60);

        if (path.StartsWithSegments("/connect/deviceauthorization"))
            return ("device_authorization", 20);

        if (path.StartsWithSegments("/connect/introspect"))
            return ("introspection", 120);

        if (path.StartsWithSegments("/connect/revocation"))
            return ("revocation", 60);

        if (path.StartsWithSegments("/device"))
            return ("device_verification", 30);

        return (null, 0);
    }
}
