using Microsoft.Extensions.Options;
using NexusAuth.Extension;
using NexusAuth.Workbench.Api.Authentication.Events;
using NexusAuth.Workbench.Api.Options;

namespace NexusAuth.Workbench.Api.Authentication;

/// <summary>通过 NexusAuth.Extension SDK 对接 NexusAuth.Host，只保留 Workbench 自身的策略。</summary>
public static class WorkbenchAuthenticationExtensions
{
    /// <summary>绑定 Auth 配置节并注册 SDK；Cookie 安全策略沿用 Security 配置节。</summary>
    public static IServiceCollection AddWorkbenchAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // 先于 SDK 绑定配置注册默认值，Auth:Session:CookieName 仍可覆盖。
        services.Configure<NexusAuthClientOptions>(options =>
            options.Session.CookieName = WorkbenchAuthenticationDefaults.SessionCookieName);
        services.AddNexusAuthClient(configuration.GetSection(NexusAuthClientOptions.SectionName), setup =>
        {
            setup.DefaultScheme = WorkbenchAuthenticationDefaults.Scheme;
            setup.CookieScheme = WorkbenchAuthenticationDefaults.CookieScheme;
            setup.OpenIdConnectScheme = WorkbenchAuthenticationDefaults.OpenIdConnectScheme;
            setup.BearerScheme = WorkbenchAuthenticationDefaults.BearerScheme;
            setup.BackchannelHttpClientName = WorkbenchAuthenticationDefaults.BackchannelHttpClient;
            setup.JwtBearerEventsType = typeof(WorkbenchJwtBearerEvents);
        });
        services.AddOptions<NexusAuthClientOptions>()
            .PostConfigure<IOptions<WorkbenchSecurityOptions>>((options, security) =>
                // 默认仅 HTTPS；本地 HTTP 调试需要在 Security 配置节中显式放开。
                options.Session.CookieSecurePolicy = WorkbenchSecurityOptions.ResolveCookieSecurePolicy(security.Value));
        return services;
    }
}
