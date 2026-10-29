using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NexusAuth.Extension;

namespace NexusAuth.Workbench.Api;

/// <summary>组装 Workbench 的 Cookie 会话、标准 OIDC 登录与 BFF 会话服务。</summary>
public static class WorkbenchAuthenticationExtensions
{
    /// <summary>绑定配置并注册认证服务；应用策略留在 Workbench，通用协议适配由 Extension 提供。</summary>
    public static IServiceCollection AddWorkbenchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        AddAuthenticationOptions(services, configuration);
        AddSessionServices(services);
        AddAuthenticationHandlers(services);
        // 可选的启动优化：提前加载框架的发现文档和签名密钥，失败时后台重试。
        services.AddOpenIdConnectMetadataWarmup();
        return services;
    }

    private static void AddAuthenticationOptions(IServiceCollection services, IConfiguration configuration)
    {
        // 统一规范化、校验配置。所有使用方通过 IOptions<T> 取值，不注入配置对象本身。
        services.AddOptions<WorkbenchAuthenticationOptions>()
            .Bind(configuration.GetSection(WorkbenchAuthenticationOptions.SectionName))
            .PostConfigure(options => options.Normalize())
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<WorkbenchAuthenticationOptions>, WorkbenchAuthenticationOptionsValidator>());
        // 这是应用额外的浏览器绑定和一次性登录流程，不替代框架的 state / nonce / PKCE。
        services.AddOpenIdConnectFlowState().UseConfiguration(configuration.GetSection("Auth:FlowState"));
    }

    private static void AddSessionServices(IServiceCollection services)
    {
        // 事件和令牌操作按请求创建，处理 Workbench 的回调、JSON 契约及会话撤销检查。
        services.AddScoped<WorkbenchCookieAuthenticationEvents>();
        services.AddScoped<WorkbenchOpenIdConnectEvents>();
        services.AddScoped<IWorkbenchTokenClient, WorkbenchTokenClient>();
        // 合并并发刷新，避免同一个旋转 refresh token 被多个请求重复消费；内部创建 scope 执行请求。
        services.AddSingleton<IWorkbenchTokenRefreshCoordinator, WorkbenchTokenRefreshCoordinator>();
        // 同一个后台客户端供原生授权码兑换和 BFF 令牌操作使用，保持已注册的 Basic 认证方式。
        services.AddHttpClient(WorkbenchTokenClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .UseOpenIdConnectClientSecretBasic(WorkbenchAuthenticationDefaults.OpenIdConnectScheme);
    }

    private static void AddAuthenticationHandlers(IServiceCollection services)
    {
        // Cookie 表示本应用的登录会话；只有显式 challenge 时才跳到 OIDC Provider。
        services.AddAuthentication(WorkbenchAuthenticationDefaults.CookieScheme)
            .AddCookie(WorkbenchAuthenticationDefaults.CookieScheme, options =>
            {
                options.Cookie.Name = ".NexusAuth.Workbench";
                options.Cookie.HttpOnly = true; // 禁止脚本读取会话 Cookie。
                options.Cookie.SameSite = SameSiteMode.Lax; // 允许 Provider 通过顶层 GET 回调携带 Cookie。
                options.LoginPath = "/api/auth/login";
                options.SlidingExpiration = true; // 持续活动时续期，24 小时是 Workbench 的会话策略。
                options.ExpireTimeSpan = TimeSpan.FromHours(24);
                options.EventsType = typeof(WorkbenchCookieAuthenticationEvents);
            })
            .AddOpenIdConnect(WorkbenchAuthenticationDefaults.OpenIdConnectScheme, options =>
                options.EventsType = typeof(WorkbenchOpenIdConnectEvents));
        services.AddOptions<CookieAuthenticationOptions>(WorkbenchAuthenticationDefaults.CookieScheme)
            .Configure<IOptions<WorkbenchSecurityOptions>>((options, security) =>
                // 默认仅 HTTPS；本地 HTTP 调试需要在安全配置中显式放开。
                options.Cookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value));
        services.AddOptions<OpenIdConnectOptions>(WorkbenchAuthenticationDefaults.OpenIdConnectScheme)
            .Configure<IOptions<WorkbenchAuthenticationOptions>, IHttpClientFactory,
                IOptions<WorkbenchSecurityOptions>, IOptions<OpenIdConnectFlowStateOptions>>(
                (options, auth, http, security, flow) =>
                {
                    ConfigureOpenIdConnect(options, auth.Value, security.Value, flow.Value,
                        http.CreateClient(WorkbenchTokenClient.HttpClientName));
                });
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options,
        WorkbenchAuthenticationOptions settings, WorkbenchSecurityOptions security,
        OpenIdConnectFlowStateOptions flow, HttpClient backchannel)
    {
        // 登录成功后写入本应用 Cookie；Provider 登出也使用同一会话读取 ID Token。
        options.SignInScheme = WorkbenchAuthenticationDefaults.CookieScheme;
        options.SignOutScheme = WorkbenchAuthenticationDefaults.CookieScheme;
        options.Authority = settings.Authority; // 框架由此发现端点、缓存元数据并刷新签名密钥。
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        options.CallbackPath = new Uri(settings.RedirectUri).AbsolutePath; // 中间件拦截的回调路径。

        // 授权码 + PKCE：由中间件生成 verifier、兑换 code 并校验 ID Token；GET 回调适配当前前端。
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = true; // 保存到受保护的认证票据，供本 BFF 刷新、内省和登出撤销使用。
        options.MapInboundClaims = false; // 保留 sub/name 等原始 claim 名称，由应用统一生成 Principal。
        options.DisableTelemetry = true; // 不向 Provider 发送组件版本遥测参数。
        // 保留普通授权请求策略；PAR 是可选的协议能力，本次不启用。
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.Scope.Clear(); // 以校验后的应用 scope 为准，包含规范化时补入的业务 Audience。
        foreach (var scope in settings.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(scope);

        // 这些是 Workbench 的信任策略，不能强制作为所有 SDK 使用者的全局默认值。
        options.TokenValidationParameters.ValidIssuer = settings.Authority;
        options.TokenValidationParameters.ValidAudience = settings.ClientId; // ID Token 的受众是客户端，不是 API Audience。
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30); // 允许有限的服务器时钟偏差。
        options.TokenValidationParameters.NameClaimType = "name";
        options.Backchannel = backchannel; // 复用 HttpClientFactory 的超时、连接池与 Basic 适配。

        // correlation 绑定发起浏览器，nonce 防止 ID Token 重放；两种 Cookie 仍由框架生成和校验。
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security);
        options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy;
        options.RemoteAuthenticationTimeout = flow.Lifetime; // 框架认证票据与额外流程状态使用相同有效期。
    }
}
