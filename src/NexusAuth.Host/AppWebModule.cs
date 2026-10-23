using NexusAuth.Application.Clients.Authentication;
using NexusAuth.Shared.DependencyInjection;
using NexusAuth.Host.TokenEndpoint;
using Luck.AppModule;
using Luck.AutoDependencyInjection;
using Luck.Framework.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using NexusAuth.Application.Services;
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
        services.AddOptions<NexusAuthSecurityOptions>()
            .Bind(configuration.GetSection("Security"))
            .Validate(
                options => TryParseCookieSecurePolicy(options.CookieSecurePolicy, out _),
                "Security:CookieSecurePolicy must be SameAsRequest or Always.")
            .ValidateOnStart();
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
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/account/login";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.EventsType = typeof(ISsoCookieAuthenticationEvents);
            });
        services.AddAuthorization();

        // 中文注释：SSO Cookie 的 Secure 策略由 Security:CookieSecurePolicy 控制。
        // 默认 SameAsRequest 以保证本地 HTTP 调试可用；部署在 TLS 反向代理之后但未启用转发头时，
        // 请求在应用看来是 HTTP，Cookie 会缺少 Secure 标记，因此生产环境应配置为 Always。
        services.AddOptions<CookieAuthenticationOptions>(AuthenticationScheme)
            .Configure<IOptions<NexusAuthSecurityOptions>>((options, security) =>
                options.Cookie.SecurePolicy = ResolveCookieSecurePolicy(security.Value));
        // 中文注释：防伪 Cookie 默认从不带 Secure，这里改为 HTTPS 请求时始终带 Secure。
        // 不跟随 Always：ASP.NET Core 防伪组件在 Always 且请求不是 HTTPS 时会直接抛异常，
        // 会让未配置转发头的 TLS 代理部署中所有表单页面不可用。
        // X-Frame-Options 由统一的安全头中间件输出 DENY，因此关闭防伪组件自带的 SAMEORIGIN。
        services.Configure<AntiforgeryOptions>(options =>
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SuppressXFrameOptionsHeader = true;
        });

        base.ConfigureServices(context);
    }

    /// <summary>
    /// 配置应用初始化中间件管道。
    /// </summary>
    public override void ApplicationInitialization(ApplicationContext context)
    {
        var app = context.GetApplicationBuilder();
        // 中文注释：HSTS 中间件只会对 HTTPS 请求（含已正确配置转发头的代理请求）输出头部，开发环境不启用。
        if (!app.ApplicationServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            app.UseHsts();
        app.Use(static (httpContext, next) =>
        {
            httpContext.Response.OnStarting(static state => ApplySecurityHeaders((HttpContext)state), httpContext);
            return next(httpContext);
        });
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        base.ApplicationInitialization(context);
    }

    /// <summary>
    /// 中文注释：为 Host 的所有动态响应补充防点击劫持、防 MIME 嗅探和 Referrer 策略头。
    /// 只设置 frame-ancestors，不限制 script-src，避免影响现有页面中的内联脚本。
    /// X-Frame-Options 始终为 DENY；其余头部若已由下游组件显式设置则保持不变。
    /// </summary>
    private static Task ApplySecurityHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XFrameOptions = "DENY";
        headers.TryAdd("Content-Security-Policy", "frame-ancestors 'none'");
        headers.TryAdd("X-Content-Type-Options", "nosniff");

        // 授权/同意流程的跳转地址可能携带授权码或 state，这些端点完全不发送 Referer。
        var path = context.Request.Path;
        var referrerPolicy = path.StartsWithSegments("/connect") || path.StartsWithSegments("/consent")
            ? "no-referrer"
            : "strict-origin-when-cross-origin";
        headers.TryAdd("Referrer-Policy", referrerPolicy);
        return Task.CompletedTask;
    }

    internal static CookieSecurePolicy ResolveCookieSecurePolicy(NexusAuthSecurityOptions options) =>
        TryParseCookieSecurePolicy(options.CookieSecurePolicy, out var policy)
            ? policy
            : throw new InvalidOperationException("Security:CookieSecurePolicy must be SameAsRequest or Always.");

    private static bool TryParseCookieSecurePolicy(string? value, out CookieSecurePolicy policy)
    {
        // CookieSecurePolicy.None 会让 Cookie 永远不带 Secure，属于降级配置，因此不允许。
        if (string.IsNullOrWhiteSpace(value))
        {
            policy = CookieSecurePolicy.SameAsRequest;
            return true;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out policy)
            && policy is CookieSecurePolicy.SameAsRequest or CookieSecurePolicy.Always
            && !int.TryParse(value, out _))
            return true;

        policy = CookieSecurePolicy.SameAsRequest;
        return false;
    }

    private static RateLimitPartition<string> CreateRateLimitPartition(HttpContext context)
    {
        var path = context.Request.Path;
        var (family, tokenLimit) = GetRateLimitFamily(path, context.Request.Method);
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

    private static (string? Family, int TokenLimit) GetRateLimitFamily(PathString path, string method)
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

        // 修改密码与身份验证器管理的提交都会校验当前密码/动态码，纳入独立的限流族；
        // 页面浏览（GET/HEAD）不是校验入口，不占用配额，避免影响个人中心的页面切换。
        if ((path.StartsWithSegments("/account/change-password") || path.StartsWithSegments("/account/two-factor"))
            && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            return ("account_security", 10);

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
