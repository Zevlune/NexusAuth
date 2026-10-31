using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace NexusAuth.Extension;

/// <summary>对接 NexusAuth.Host 的 BFF 客户端配置，默认绑定 <c>Auth</c> 配置节。</summary>
public sealed class NexusAuthClientOptions
{
    /// <summary>默认配置节名称。</summary>
    public const string SectionName = "Auth";

    /// <summary>NexusAuth.Host 的 Issuer 地址，必须与发现文档的 issuer 完全一致。</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>本应用 API 资源的受众；启用 Bearer 时必填，并自动补入 Scope。</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>在 NexusAuth.Host 注册的机密客户端 ID。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>客户端密钥，只用于后台通道。</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>完整回调地址；其路径即标准 OIDC 中间件拦截的 CallbackPath。</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Provider 登出后的返回地址；未设置 FrontendUri 时也作为前端根地址。</summary>
    public string PostLogoutRedirectUri { get; set; } = string.Empty;

    /// <summary>以空格分隔的 scope；会话刷新依赖 offline_access。</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>登出时是否同时结束 NexusAuth.Host 的登录会话。</summary>
    public bool SignOutProvider { get; set; } = true;

    /// <summary>发现文档与后台端点是否必须使用 HTTPS。</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>后台通道的客户端认证方式：ClientSecretBasic 或 ClientSecretPost。</summary>
    public NexusAuthClientAuthentication ClientAuthentication { get; set; } = NexusAuthClientAuthentication.ClientSecretBasic;

    /// <summary>ID Token 与 Access Token 允许的签名算法，默认仅 RS256。</summary>
    public string[] ValidAlgorithms { get; set; } = [SecurityAlgorithms.RsaSha256];

    /// <summary>除本应用 ClientId 外，允许以 Bearer 调用本应用 API 的 OAuth 客户端。</summary>
    public string[] AllowedBearerClientIds { get; set; } = [];

    /// <summary>浏览器会话配置。</summary>
    public NexusAuthSessionOptions Session { get; set; } = new();

    /// <summary>前端回调与错误页配置。</summary>
    public NexusAuthFrontendOptions Frontend { get; set; } = new();

    /// <summary>多实例共享的 Redis 连接。</summary>
    public NexusAuthRedisOptions Redis { get; set; } = new();

    /// <summary>刷新令牌的并发协调方式。</summary>
    public NexusAuthTokenRefreshOptions TokenRefresh { get; set; } = new();

    /// <summary>Data Protection 密钥环的持久化方式。</summary>
    public NexusAuthDataProtectionOptions DataProtection { get; set; } = new();

    internal string FrontendBaseUri =>
        (string.IsNullOrWhiteSpace(Frontend.BaseUri) ? PostLogoutRedirectUri : Frontend.BaseUri).TrimEnd('/');

    internal void Normalize()
    {
        Authority = Authority?.Trim().TrimEnd('/') ?? string.Empty;
        Audience = Audience?.Trim() ?? string.Empty;
        ClientId = ClientId?.Trim() ?? string.Empty;
        ClientSecret = ClientSecret?.Trim() ?? string.Empty;
        RedirectUri = RedirectUri?.Trim() ?? string.Empty;
        PostLogoutRedirectUri = PostLogoutRedirectUri?.Trim() ?? string.Empty;
        Frontend.BaseUri = Frontend.BaseUri?.Trim() ?? string.Empty;
        AllowedBearerClientIds = Distinct(AllowedBearerClientIds);
        ValidAlgorithms = Distinct(ValidAlgorithms);
        if (!string.IsNullOrWhiteSpace(Scope))
        {
            var scopes = Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToList();
            if (!string.IsNullOrEmpty(Audience) && !scopes.Contains(Audience, StringComparer.Ordinal))
                scopes.Add(Audience);
            Scope = string.Join(' ', scopes);
        }
    }

    private static string[] Distinct(string[]? values) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>后台通道发送客户端凭据的方式。</summary>
public enum NexusAuthClientAuthentication
{
    /// <summary>HTTP Basic 认证头。</summary>
    ClientSecretBasic,

    /// <summary>表单字段 client_id / client_secret。</summary>
    ClientSecretPost,
}

/// <summary>本应用自身的 Cookie 会话策略。</summary>
public sealed class NexusAuthSessionOptions
{
    /// <summary>会话 Cookie 名称；同域部署多个应用时应各不相同。</summary>
    public string CookieName { get; set; } = ".NexusAuth.Session";

    /// <summary>Cookie 安全策略：Always 或 SameAsRequest（仅限本地 HTTP 调试）。</summary>
    public CookieSecurePolicy CookieSecurePolicy { get; set; } = CookieSecurePolicy.Always;

    /// <summary>滑动会话有效期。</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Access Token 剩余有效期低于该值时刷新。</summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>每次请求是否内省 Access Token，以便即时感知管理员撤销与用户禁用。</summary>
    public bool ValidateWithIntrospection { get; set; } = true;
}

/// <summary>前端（SPA）地址约定。</summary>
public sealed class NexusAuthFrontendOptions
{
    /// <summary>前端根地址；为空时使用 PostLogoutRedirectUri。</summary>
    public string BaseUri { get; set; } = string.Empty;

    /// <summary>登录完成后返回的前端路由。</summary>
    public string CallbackPath { get; set; } = "/auth/callback";

    /// <summary>登录失败时跳转的前端路由，附带 error 查询参数。</summary>
    public string LoginPath { get; set; } = "/login";
}

/// <summary>SDK 各集群组件共享的 Redis 连接。</summary>
public sealed class NexusAuthRedisOptions
{
    /// <summary>StackExchange.Redis 连接字符串；已注册 IConnectionMultiplexer 时可留空。</summary>
    public string? Configuration { get; set; }

    /// <summary>SDK 写入的键前缀；不同应用应使用不同前缀。</summary>
    public string KeyPrefix { get; set; } = "NexusAuth";
}

/// <summary>刷新令牌并发协调。</summary>
public sealed class NexusAuthTokenRefreshOptions
{
    /// <summary>InMemory 仅合并单实例内的并发刷新；Redis 跨实例加锁并共享刷新结果。</summary>
    public string Provider { get; set; } = NexusAuthProviders.InMemory;

    /// <summary>刷新结果的共享时长，覆盖并发请求携带旧 Cookie 的窗口。</summary>
    public TimeSpan ResultLifetime { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Data Protection 密钥环。PKCE verifier、state、correlation/nonce Cookie 与会话 Cookie 都由它加密，
/// 多实例必须共享同一密钥环和 ApplicationName。
/// </summary>
public sealed class NexusAuthDataProtectionOptions
{
    /// <summary>None 表示由应用自行配置；也可选择 Redis 或 FileSystem。</summary>
    public string Provider { get; set; } = NexusAuthProviders.None;

    /// <summary>所有实例一致的应用名，隔离不同应用的加密用途。</summary>
    public string? ApplicationName { get; set; }

    /// <summary>FileSystem 模式下的共享目录。</summary>
    public string? KeyPath { get; set; }
}

/// <summary>SDK 配置中可选的存储提供程序名称。</summary>
public static class NexusAuthProviders
{
    /// <summary>不由 SDK 配置。</summary>
    public const string None = "None";

    /// <summary>进程内。</summary>
    public const string InMemory = "InMemory";

    /// <summary>Redis。</summary>
    public const string Redis = "Redis";

    /// <summary>共享文件目录。</summary>
    public const string FileSystem = "FileSystem";
}
