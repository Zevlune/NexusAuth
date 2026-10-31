using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NexusAuth.Extension;

/// <summary>一次注册对接 NexusAuth.Host 所需的 Cookie 会话、OIDC 授权码 + PKCE 登录、JWT Bearer 与令牌服务。</summary>
public static class NexusAuthClientExtensions
{
    /// <summary>绑定根配置中的 <c>Auth</c> 节并注册客户端。</summary>
    public static AuthenticationBuilder AddNexusAuthClient(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NexusAuthClientSetup>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddNexusAuthClient(configuration.GetSection(NexusAuthClientOptions.SectionName), configure);
    }

    /// <summary>绑定指定配置节并注册客户端；方案名称与事件类型通过 <paramref name="configure"/> 调整。</summary>
    public static AuthenticationBuilder AddNexusAuthClient(
        this IServiceCollection services,
        IConfigurationSection section,
        Action<NexusAuthClientSetup>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(NexusAuthClientSetup)))
            throw new InvalidOperationException("AddNexusAuthClient can only be called once per application.");

        var setup = new NexusAuthClientSetup();
        configure?.Invoke(setup);
        setup.Validate();
        services.AddSingleton(setup);

        AddOptions(services, section, setup);
        AddServices(services, setup);
        var builder = AddHandlers(services, setup);
        // 可选的启动优化：提前加载发现文档和签名密钥，失败时后台重试。
        services.AddOpenIdConnectMetadataWarmup();
        return builder;
    }

    private static void AddOptions(IServiceCollection services, IConfigurationSection section, NexusAuthClientSetup setup)
    {
        // 共享连接必须先于流程状态注册，Redis 流程存储才会复用它而不是另建连接。
        var redisRegistered = NexusAuthClusterRegistration.AddConnection(services, section);
        services.AddOptions<NexusAuthClientOptions>()
            .Bind(section)
            .PostConfigure(options => options.Normalize())
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NexusAuthClientOptions>>(provider =>
            new NexusAuthClientOptionsValidator(section.Path, setup.EnableBearer, redisRegistered,
                provider.GetRequiredService<IOptions<RedisFlowStateOptions>>()));
        // 应用级的浏览器绑定与一次性流程，叠加在框架的 state / nonce / PKCE 之上。
        services.AddOpenIdConnectFlowState().UseConfiguration(section.GetSection("FlowState"));
        NexusAuthClusterRegistration.AddDataProtection(services, section);
    }

    private static void AddServices(IServiceCollection services, NexusAuthClientSetup setup)
    {
        // 事件和令牌操作按请求创建；刷新协调器为单例，内部创建 scope 执行兑换。
        services.TryAddScoped(setup.CookieEventsType);
        services.TryAddScoped(setup.OpenIdConnectEventsType);
        services.TryAddScoped(setup.JwtBearerEventsType);
        services.TryAddScoped<INexusAuthTokenClient, NexusAuthTokenClient>();
        NexusAuthClusterRegistration.AddTokenRefresh(services);
        // 同一个后台客户端供原生授权码兑换和 BFF 令牌操作使用，保持一致的客户端认证方式。
        services.AddHttpClient(setup.BackchannelHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler(provider => new ClientSecretBasicHandler(
                provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(),
                setup.OpenIdConnectScheme,
                () => provider.GetRequiredService<IOptions<NexusAuthClientOptions>>().Value.ClientAuthentication
                    == NexusAuthClientAuthentication.ClientSecretBasic));
    }

    private static AuthenticationBuilder AddHandlers(IServiceCollection services, NexusAuthClientSetup setup)
    {
        // 浏览器会话用 Cookie，显式提供 Authorization 头的 API 调用只走 Bearer。
        // 无效或空的 Bearer 不能回退到 Cookie，否则会掩盖 API 凭据错误。
        var builder = services.AddAuthentication(setup.DefaultScheme)
            .AddPolicyScheme(setup.DefaultScheme, "Cookie or Bearer", options =>
                options.ForwardDefaultSelector = context =>
                    setup.EnableBearer && context.Request.Headers.ContainsKey("Authorization")
                        ? setup.BearerScheme
                        : setup.CookieScheme)
            .AddCookie(setup.CookieScheme, options => options.EventsType = setup.CookieEventsType)
            .AddOpenIdConnect(setup.OpenIdConnectScheme, options => options.EventsType = setup.OpenIdConnectEventsType);
        if (setup.EnableBearer)
        {
            builder.AddJwtBearer(setup.BearerScheme, options => options.EventsType = setup.JwtBearerEventsType);
            AddBearerOptions(services, setup);
        }

        services.AddOptions<CookieAuthenticationOptions>(setup.CookieScheme)
            .Configure<IOptions<NexusAuthClientOptions>>((options, client) =>
            {
                var session = client.Value.Session;
                options.Cookie.Name = session.CookieName;
                options.Cookie.HttpOnly = true; // 禁止脚本读取会话 Cookie。
                options.Cookie.SameSite = SameSiteMode.Lax; // 允许 Provider 通过顶层 GET 回调携带 Cookie。
                options.Cookie.SecurePolicy = session.CookieSecurePolicy;
                options.LoginPath = NexusAuthEndpoints.DefaultPrefix + "/login";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = session.Lifetime;
            });
        services.AddOptions<OpenIdConnectOptions>(setup.OpenIdConnectScheme)
            .Configure<IOptions<NexusAuthClientOptions>, IHttpClientFactory, IOptions<OpenIdConnectFlowStateOptions>>(
                (options, client, http, flow) => ConfigureOpenIdConnect(options, client.Value, setup, flow.Value,
                    http.CreateClient(setup.BackchannelHttpClientName)));
        return builder;
    }

    private static void AddBearerOptions(IServiceCollection services, NexusAuthClientSetup setup)
    {
        services.AddOptions<JwtBearerOptions>(setup.BearerScheme)
            .Configure<IOptions<NexusAuthClientOptions>, IOptionsMonitor<OpenIdConnectOptions>>(
                (options, client, oidcOptions) =>
                {
                    var settings = client.Value;
                    var oidc = oidcOptions.Get(setup.OpenIdConnectScheme);
                    options.Authority = settings.Authority;
                    options.Audience = settings.Audience;
                    options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                    // API 验签与 OIDC 登录复用框架的发现文档和签名密钥缓存。
                    options.Configuration = oidc.Configuration;
                    options.ConfigurationManager = oidc.ConfigurationManager;
                    options.MapInboundClaims = false;
                    options.SaveToken = true;
                    options.IncludeErrorDetails = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = settings.Authority,
                        ValidateAudience = true,
                        ValidAudience = settings.Audience, // Access Token 的受众是 API resource。
                        ValidateIssuerSigningKey = true,
                        RequireSignedTokens = true,
                        ValidateLifetime = true,
                        RequireExpirationTime = true,
                        ValidAlgorithms = settings.ValidAlgorithms,
                        ClockSkew = TimeSpan.FromSeconds(30),
                        NameClaimType = "name",
                        RoleClaimType = "role",
                    };
                });
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options, NexusAuthClientOptions settings,
        NexusAuthClientSetup setup, OpenIdConnectFlowStateOptions flow, HttpClient backchannel)
    {
        // 登录成功后写入本应用 Cookie；Provider 登出也使用同一会话读取 ID Token。
        options.SignInScheme = setup.CookieScheme;
        options.SignOutScheme = setup.CookieScheme;
        options.Authority = settings.Authority; // 框架由此发现端点、缓存元数据并刷新签名密钥。
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
        if (Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var redirectUri))
            options.CallbackPath = redirectUri.AbsolutePath; // 中间件拦截的回调路径。

        // 授权码 + PKCE(S256)：由中间件生成 code_verifier 并随 Data Protection 加密的 state 往返，
        // 兑换 code 并校验 ID Token。集群部署时所有实例必须共享密钥环才能解开 state。
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = true; // 保存到受保护的认证票据，供刷新、内省和登出撤销使用。
        options.MapInboundClaims = false; // 保留 sub/name 等原始 claim 名称，由 SDK 统一生成 Principal。
        options.DisableTelemetry = true; // 不向 Provider 发送组件版本遥测参数。
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.Scope.Clear(); // 以校验后的应用 scope 为准，包含规范化时补入的业务 Audience。
        foreach (var scope in settings.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(scope);

        options.TokenValidationParameters.ValidIssuer = settings.Authority;
        options.TokenValidationParameters.ValidAudience = settings.ClientId; // ID Token 的受众是客户端，不是 API Audience。
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.ValidAlgorithms = settings.ValidAlgorithms;
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30); // 允许有限的服务器时钟偏差。
        options.TokenValidationParameters.NameClaimType = "name";
        options.Backchannel = backchannel; // 复用 HttpClientFactory 的超时、连接池与客户端认证适配。

        // correlation 绑定发起浏览器，nonce 防止 ID Token 重放；两种 Cookie 仍由框架生成和校验。
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = settings.Session.CookieSecurePolicy;
        options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy;
        options.RemoteAuthenticationTimeout = flow.Lifetime; // 框架认证票据与额外流程状态使用相同有效期。
    }
}
