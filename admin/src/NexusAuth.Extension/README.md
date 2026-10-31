# NexusAuth.Extension

`NexusAuth.Extension` 是对接 NexusAuth.Host 的 ASP.NET Core 客户端 SDK。新项目引用它并填写 `Auth` 配置节，即可获得与 Workbench 相同的登录能力：

- 标准 `AddOpenIdConnect` 授权码 + PKCE(S256) 登录，`client_secret_basic` 或 `client_secret_post` 后台认证；
- HttpOnly Cookie BFF 会话：滑动续期、临近过期自动刷新、每次请求内省以感知撤销；
- 可选 JWT Bearer：只接受 Access Token，限定受众与调用客户端，与 OIDC 共享元数据和签名密钥缓存；
- `GET config`、`GET login`、`POST logout`、`GET me` 端点，登出时撤销令牌并结束 Provider 会话；
- 一次性浏览器绑定的登录流程状态（InMemory / Redis），以及多实例部署所需的 Redis 连接、Data Protection 密钥环与跨实例刷新协调。

协议本身（state、nonce、PKCE verifier、授权码兑换、ID Token 验签）全部由框架的 OIDC 中间件完成，SDK 不重复实现。

## 快速接入

```csharp
using NexusAuth.Extension;

builder.Services.AddNexusAuthClient(builder.Configuration); // 绑定 Auth 配置节
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapNexusAuthEndpoints(); // /api/auth/config、login、logout、me
```

```json
{
  "Auth": {
    "Authority": "https://sso.example.com",
    "ClientId": "my-app",
    "ClientSecret": "<secret>",
    "Audience": "my-app.api",
    "RedirectUri": "https://my-app.example.com/signin-oidc",
    "PostLogoutRedirectUri": "https://my-app.example.com/",
    "Scope": "openid profile offline_access"
  }
}
```

在 NexusAuth.Host（或 Workbench 应用管理）中登记同一个客户端：授权码 + refresh_token 授权、上述 RedirectUri 与 PostLogoutRedirectUri、`ClientSecretBasic` 认证，并关联 `Audience` 对应的 API 资源。`Audience` 会自动补入 Scope。

前端流程：

1. `GET /api/auth/login` 返回 `{ authorizeUrl }`，前端 `window.location = authorizeUrl`。可带 `?returnUrl=/some/page`，只接受站内路径。
2. NexusAuth.Host 回调 `RedirectUri`（默认 `/signin-oidc`，由 OIDC 中间件处理），成功后写入会话 Cookie 并跳回 `Frontend:BaseUri + Frontend:CallbackPath`（默认 `PostLogoutRedirectUri` + `/auth/callback`）。
3. 失败时跳到 `{前端}/login?error=invalid_callback|token_exchange_failed`；Provider 拒绝（例如 `access_denied`）跳到 `{Authority}/oauth/error`。
4. `GET /api/auth/me` 查询登录状态，`POST /api/auth/logout` 返回 `{ logoutUrl }`，前端跳转以结束 Provider 会话。

服务端渲染应用可让登录接口直接 302：`app.MapNexusAuthEndpoints(configure: o => o.LoginResponse = NexusAuthLoginResponse.Redirect)`。

## 配置项

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `Authority` | 必填 | NexusAuth.Host Issuer，须与发现文档 issuer 完全一致 |
| `ClientId` / `ClientSecret` | 必填 | 机密客户端凭据，只用于后台通道 |
| `Audience` | 启用 Bearer 时必填 | Access Token 受众；自动加入 Scope |
| `RedirectUri` | 必填 | 完整回调地址，路径即 CallbackPath |
| `PostLogoutRedirectUri` | 必填 | Provider 登出后返回地址，也是默认前端根地址 |
| `Scope` | 必填 | 会话续期依赖 `offline_access` |
| `RequireHttpsMetadata` | `true` | 本地 HTTP 调试才关闭 |
| `SignOutProvider` | `true` | 登出时同时结束 Provider 会话 |
| `ClientAuthentication` | `ClientSecretBasic` | 或 `ClientSecretPost` |
| `ValidAlgorithms` | `["RS256"]` | ID Token 与 Access Token 允许的签名算法 |
| `AllowedBearerClientIds` | `[]` | 除本应用外允许调用 API 的客户端 |
| `Session:CookieName` | `.NexusAuth.Session` | 同域多个应用须各不相同 |
| `Session:CookieSecurePolicy` | `Always` | 本地 HTTP 调试可设 `SameAsRequest` |
| `Session:Lifetime` | `1.00:00:00` | 滑动会话有效期 |
| `Session:RefreshBeforeExpiry` | `00:01:00` | Access Token 剩余时间低于该值时刷新 |
| `Session:ValidateWithIntrospection` | `true` | 每次请求内省，管理员撤销后立即失效 |
| `Frontend:BaseUri` | 空（取 `PostLogoutRedirectUri`） | 前端根地址 |
| `Frontend:CallbackPath` / `LoginPath` | `/auth/callback` / `/login` | 登录成功与失败的前端路由 |
| `FlowState:*` | `InMemory` | 见下文“集群部署” |
| `Redis:Configuration` / `KeyPrefix` | 空 / `NexusAuth` | SDK 共享的 Redis 连接 |
| `TokenRefresh:Provider` | `InMemory` | 或 `Redis` |
| `DataProtection:Provider` | `None` | 或 `Redis`、`FileSystem` |

所有配置在启动时校验，错误信息带完整配置路径，例如 `Auth:Authority is required in appsettings configuration.`。

## 集群部署

单实例只需上述配置。多实例（含滚动发布、负载均衡无会话粘滞）时，以下三类状态必须跨实例共享：

| 状态 | 不共享的后果 | 配置 |
| --- | --- | --- |
| Data Protection 密钥环 | PKCE code_verifier 随加密的 `state` 往返，回调落到另一实例时无法解密，登录失败；会话 Cookie 也无法互认 | `DataProtection:Provider=Redis` 或 `FileSystem`，并设置相同的 `ApplicationName` |
| 登录流程状态 | 浏览器绑定只存在发起登录的实例，回调落到其他实例被判为 `invalid_callback` | `FlowState:Provider=Redis` |
| 刷新协调 | 多个实例同时用同一 refresh token 刷新，旋转令牌被重复消费，Provider 判定重放并撤销整个令牌族，用户被登出 | `TokenRefresh:Provider=Redis` |

```json
{
  "Auth": {
    "Redis": { "Configuration": "redis:6379,password=<secret>", "KeyPrefix": "NexusAuth:my-app" },
    "FlowState": { "Provider": "Redis" },
    "TokenRefresh": { "Provider": "Redis" },
    "DataProtection": { "Provider": "Redis", "ApplicationName": "my-app" }
  }
}
```

- `Auth:Redis` 注册一个共享的 `IConnectionMultiplexer`，流程状态、刷新协调与密钥环都复用它；应用已自行注册 `IConnectionMultiplexer` 时可留空。旧配置 `FlowState:Redis:Configuration` 仍然有效。
- Redis 刷新协调：同一 refresh token 只由持有锁的实例兑换，结果用 Data Protection 加密后在 Redis 保存 `TokenRefresh:ResultLifetime`（默认 30 秒），其余实例直接复用。键中只出现令牌的 SHA-256 摘要，不保存明文令牌。
- 密钥环选 `FileSystem` 时，`KeyPath` 必须是所有实例挂载的同一共享目录。需要静态加密密钥时，可在调用 SDK 后继续使用 `services.AddDataProtection().ProtectKeysWith...()`。
- `DataProtection:Provider=None` 表示由应用自行调用 `AddDataProtection()`，适合已有统一密钥管理的项目。
- 反向代理部署时，`RedirectUri` 使用对外的完整地址，SDK 会把它原样写入授权请求，不依赖节点自身的 Host/Scheme。Cookie 的 Secure 判断依赖 `Request.IsHttps`，TLS 在代理终止时应用需配置 `ForwardedHeaders`。

## 扩展点

`AddNexusAuthClient` 的第二个参数在注册阶段调整方案名称和事件类型：

```csharp
builder.Services.AddNexusAuthClient(builder.Configuration, setup =>
{
    setup.CookieScheme = "MyApp.Cookie";       // 默认 NexusAuth.Cookie
    setup.EnableBearer = false;                // 纯浏览器应用可关闭 Bearer，此时 Audience 可选
    setup.JwtBearerEventsType = typeof(MyJwtBearerEvents);
});
```

| 类型 | 用途 |
| --- | --- |
| `NexusAuthOpenIdConnectEvents` | 回调地址、签名算法、refresh token 要求、精简 Principal、错误跳转；覆盖 `CreatePrincipal` 可加入应用 claim |
| `NexusAuthCookieAuthenticationEvents` | 会话内省与刷新 |
| `NexusAuthJwtBearerEvents` | Access Token 与客户端校验；覆盖 `IsActiveAsync` 加入应用自己的撤销检查 |
| `INexusAuthTokenClient` | refresh、introspection、revoke，可在业务代码中注入使用 |
| `INexusAuthTokenRefreshCoordinator` | 刷新单飞；`NexusAuthTokenRefreshCoordinator` 或 `RedisTokenRefreshCoordinator` |

覆盖事件方法时应调用 `base`，以保留流程绑定与端点检查。`MapNexusAuthEndpoints` 返回路由组，可继续追加约定；登录接口限流使用 `options.LoginRateLimitPolicy`，需要自定义 `/me` 返回时设 `options.MapCurrentUser = false` 并自行实现。

## 底层组件

快速接入已包含以下组件，也可以在自己组装的 `AddOpenIdConnect` 上单独使用。

### Basic 认证与元数据预热

```csharp
services.AddHttpClient("client.oidc", client => client.Timeout = TimeSpan.FromSeconds(30))
    .UseOpenIdConnectClientSecretBasic("oidc");
services.AddOptions<OpenIdConnectOptions>("oidc")
    .Configure<IHttpClientFactory>((options, http) => options.Backchannel = http.CreateClient("client.oidc"));
services.AddOpenIdConnectMetadataWarmup();
```

`UseOpenIdConnectClientSecretBasic(scheme)` 在请求阶段读取该方案的 `OpenIdConnectOptions`，把表单中的客户端凭据改为 Basic 认证头；GET 发现文档和 JWKS 请求不附带凭据。`AddOpenIdConnectMetadataWarmup()` 在后台预热所有 OIDC 方案的原生 `ConfigurationManager`，失败每 30 秒重试，不阻塞启动。`OpenIdConnectEndpointValidator.Validate(uri, requireHttps)` 拒绝相对地址、非 HTTP(S)、内嵌凭据和片段。

### 流程状态

```csharp
services.AddOpenIdConnectFlowState()
    .UseConfiguration(configuration.GetSection("Auth:FlowState"));
services.AddAuthentication()
    .AddOpenIdConnect("oidc", options => options.EventsType = typeof(OpenIdConnectFlowStateEvents));
```

发起 challenge 前调用 `OpenIdConnectFlowState.BeginAsync`，失败时调用 `CancelAsync`。`MessageReceived` 原子消费流程并核对浏览器绑定 Cookie，缺失、过期、重放或绑定不匹配时让回调失败。`Provider` 区分大小写，只接受 `InMemory` 和 `Redis`；默认 TTL 10 分钟、容量 10,000，Redis 默认 KeyPrefix 为 `NexusAuth:oidc:flow`。容量耗尽时 `BeginAsync` 抛出 `FlowStateCapacityExceededException`，SDK 登录接口返回 `429`。

## 目录

```text
NexusAuth.Extension/
├── Authentication/      Basic 后台认证、端点校验、元数据预热、流程状态事件
├── Client/              AddNexusAuthClient、配置与校验、端点、Principal
│   ├── Cluster/         共享 Redis 连接、Data Protection 密钥环、Redis 刷新协调
│   ├── Events/          OIDC、Cookie、JWT Bearer 事件
│   └── Tokens/          令牌客户端、结果模型与刷新单飞
├── Flows/               流程状态存储（InMemory / Redis）
├── OpenIdConnectFlowState.cs
└── README.md
```
