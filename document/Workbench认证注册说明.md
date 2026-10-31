# Workbench 认证注册与复用边界

[`AddWorkbenchAuthentication`](../admin/src/NexusAuth.Workbench.Api/Authentication/WorkbenchAuthenticationExtensions.cs) 是本应用的组装入口。它调用 SDK 的 [`AddNexusAuthClient`](../admin/src/NexusAuth.Extension/Client/NexusAuthClientExtensions.cs)，只固定 Workbench 的方案名称和会话 Cookie 名称、沿用 `Security:CookieSecurePolicy`，并把 Bearer 事件替换为追加用户撤销检查的 `WorkbenchJwtBearerEvents`。默认方案 `Workbench` 是无 `Authorization` 头时走 Cookie、有任意 `Authorization` 头时只走 Bearer 的策略方案。具体协议行为仍配置在标准 `OpenIdConnectOptions` 和 `JwtBearerOptions` 上。

认证与业务配置统一来自 `appsettings.json` 和 `appsettings.{Environment}.json`，消费者继续通过 `IOptions<T>` 获取。项目不再注册 `NEXUSAUTH_WORKBENCH_*` 单下划线别名；ASP.NET Core 原生环境变量与命令行覆盖仍然存在，但不作为主要部署流程。

## SDK 与 Workbench 的分工

对接 NexusAuth.Host 的通用登录能力全部位于 `NexusAuth.Extension`，后续项目引用后填写 `Auth` 配置节即可使用；Workbench 只保留与自身业务数据相关的部分。

| 能力 | 放置位置 | 入口 |
| --- | --- | --- |
| Cookie + OIDC(授权码 + PKCE) + JWT Bearer 方案组装、默认策略方案 | Extension | `AddNexusAuthClient(configuration, setup => ...)`，方案名称和事件类型在 `NexusAuthClientSetup` 中调整 |
| 配置绑定、规范化与启动校验 | Extension | `NexusAuthClientOptions`，错误信息带配置路径 |
| 回调地址、签名算法限制、refresh token 要求、精简 Principal、前端错误跳转 | Extension | `NexusAuthOpenIdConnectEvents`；会话时长、算法、前端路由均可配置 |
| 会话 introspection、refresh token 轮换、并发刷新合并 | Extension | `NexusAuthCookieAuthenticationEvents`、`INexusAuthTokenRefreshCoordinator` |
| 刷新、内省、撤销的 HTTP 客户端 | Extension | `INexusAuthTokenClient`，支持 `ClientSecretBasic` / `ClientSecretPost` |
| 登录、登出、配置与 me 接口 | Extension | `MapNexusAuthEndpoints()`；Workbench 关闭 SDK 的 me 并保留自己的实现 |
| 浏览器绑定、登录流程一次性消费、内存/Redis 存储 | Extension | `AddOpenIdConnectFlowState()`，SDK 自动注册 |
| 多实例共享：Redis 连接、Data Protection 密钥环、跨实例刷新锁 | Extension | `Auth:Redis`、`Auth:DataProtection`、`Auth:TokenRefresh` |
| 客户端 Basic 认证、元数据预热、端点地址检查 | Extension | `UseOpenIdConnectClientSecretBasic`、`AddOpenIdConnectMetadataWarmup`、`OpenIdConnectEndpointValidator` |
| Bearer 用户禁用、会话撤销、token epoch、jti 黑名单 | Workbench | `WorkbenchJwtBearerEvents` 覆盖 SDK 的 `IsActiveAsync`，调用 `IUserTokenValidator` |
| `/api/auth/me` 的管理员判定、Bootstrap 客户端登记、Security/Swagger 配置 | Workbench | `CurrentIdentityController`、`WorkbenchClientCredentialHostedService` |

Extension 不依赖 `NexusAuth.Shared`、Workbench 或 Luck，也没有引入 `IdentityModel` / `Duende.IdentityModel` 客户端库。它直接扩展微软的认证和 HttpClient 注册方式。

## 注册顺序与生命周期

`AddNexusAuthClient` 先注册共享 Redis 连接（配置了 `Auth:Redis` 时），再绑定 `Auth` 与流程存储配置，通过 Options 规范化并启动校验，不会把 [`NexusAuthClientOptions`](../admin/src/NexusAuth.Extension/Client/NexusAuthClientOptions.cs) 对象直接作为单例注册。Workbench 在调用前用 `Configure` 写入 `.NexusAuth.Workbench` 作为 Cookie 名称默认值，配置中的 `Auth:Session:CookieName` 仍可覆盖。

Cookie/OIDC/JWT Bearer 事件和令牌客户端按请求范围注册。刷新协调器注册为单例，以便合并来自不同请求的同一 refresh token 刷新；实际令牌请求通过它创建的 scope 执行。`Auth:TokenRefresh:Provider=Redis` 时改用 `RedisTokenRefreshCoordinator`，在单实例合并之上再加跨实例锁。客户端认证 handler 由 HttpClientFactory 创建和回收，不需要应用再注册实现类或手工释放共享实例。

SDK 注册默认策略方案、Cookie、OpenIdConnect 和 Bearer 四个方案，Workbench 分别命名为 `Workbench`、`Workbench.Cookie`、`Workbench.OpenIdConnect` 和 `Bearer`。策略方案只在请求没有 `Authorization` 头时转发到 Cookie；只要存在该头，就只转发到 Bearer。对 `OpenIdConnectOptions` 和 `JwtBearerOptions` 的配置依赖 `IOptions<T>`，没有提前构造 ServiceProvider。客户端认证适配器到发送请求时才读取原生 Options，避免在构建 Backchannel 时递归构造同一认证方案。

本应用的配置类集中在 [`Options`](../admin/src/NexusAuth.Workbench.Api/Options)：Security、Swagger 和 Bootstrap 分别维护；自身客户端登记服务位于 [`Bootstrap`](../admin/src/NexusAuth.Workbench.Api/Bootstrap)。完整目录职责见 [Workbench API README](../admin/src/NexusAuth.Workbench.Api/README.md)。

`AddOpenIdConnectMetadataWarmup` 是可选优化。框架本身已经负责发现文档和签名密钥缓存；预热只提前触发读取，成功后不再额外轮询。各方案失败后独立重试，应用关闭可取消等待。

## 默认策略与双认证

Workbench 授权仍把管理访问策略同时设为默认策略和 fallback 策略。身份入口由默认认证方案选择，不由授权 handler 二次判断：

- Dashboard 请求不发送 `Authorization` 头，策略方案选择 `.NexusAuth.Workbench` Cookie。Cookie 验证保留 24 小时滑动会话、业务 introspection、refresh token 轮换、并发刷新合并和撤销拒绝规则。
- 无头 API 客户端发送 `Authorization: Bearer <access_token>`。即使同一请求带有有效 Cookie，也只验证 Bearer；空头、错误方案或无效 token 都直接失败，不回退 Cookie。
- Bearer issuer 是 `Auth:Authority`，audience 是 `Auth:Audience`，只接受 RS256 签名、带有效期且 `token_use=access_token` 的 access token。
- Bearer 的 `client_id` 始终允许 `Auth:ClientId`；其他客户端 ID 必须列在 `Auth:AllowedBearerClientIds` 数组中。
- 框架完成 issuer、audience、签名和时效校验后，SDK 的 `NexusAuthJwtBearerEvents` 检查 `token_use` 与客户端，`WorkbenchJwtBearerEvents` 再调用 `IUserTokenValidator` 检查用户禁用、会话撤销、token epoch 和 jti 黑名单。
- JWT Bearer 与 OIDC 登录使用同一个 `OpenIdConnectOptions.ConfigurationManager`，Discovery 与 JWKS 共用框架缓存。OIDC ID token 的 audience 是 `Auth:ClientId`，不能作为 Bearer access token。

无头调用示例：

```bash
curl -H "Authorization: Bearer $NEXUSAUTH_ACCESS_TOKEN" \
  http://localhost:5051/api/auth/me
```

`POST /api/auth/logout` 对 Bearer 调用只请求撤销本次保存的 access token，不清 Cookie、不发起 Provider 登出，并返回空 `logoutUrl`。Provider revoke 失败会记录日志，接口仍返回 HTTP 200，因此不能把成功响应理解为远端一定已完成撤销。Cookie 调用保持原行为：在 best-effort 窗口内请求撤销 refresh/access token，按配置发起 Provider 登出，并始终清理本地 Cookie。

## 流程事件

`AddOpenIdConnectFlowState()` 自动 Scoped 注册 `OpenIdConnectFlowStateEvents`。基础事件在 `MessageReceived` 中先原子消费流程并核对浏览器绑定；校验成功后调用 `base` 委托，校验失败则不执行后续用户委托。授权和 Provider 登出 redirect 只检查端点，不写应用回调。

SDK 通过 `EventsType = typeof(NexusAuthOpenIdConnectEvents)` 使用继承事件。该类调用基础检查后设置应用的 `RedirectUri` 和 `PostLogoutRedirectUri`，并提供可配置的会话时长（默认 24 小时）、签名算法限制（默认 RS256）、token 兑换错误分类及前端错误跳转。自定义事件覆盖 `MessageReceived`、两个 redirect 事件或其它需要保留检查的方法时，应调用对应 `base` 方法。Cookie 会话的刷新、introspection 与 JSON 登出契约同样由 SDK 提供（`NexusAuthCookieAuthenticationEvents`、`MapNexusAuthEndpoints`）；Workbench 只保留 Bearer 用户撤销检查与 `/api/auth/me` 的管理员判定。

## OpenIdConnectOptions 各项用途

| 配置 | Workbench 的用途 |
| --- | --- |
| `SignInScheme` / `SignOutScheme` | 成功登录写入本应用 Cookie；Provider 登出时从同一票据读取 ID Token |
| `Authority` | Provider 的身份与发现入口；后续端点、元数据及 JWKS 缓存交给框架 |
| `ClientId` / `ClientSecret` | 本应用在 Provider 的客户端凭据；Basic 适配器也从这份原生 Options 读取 |
| `RequireHttpsMetadata` | 默认要求发现和后台端点使用 HTTPS；本地 HTTP 调试需显式放开 |
| `CallbackPath` | 中间件拦截回调的本地路径；完整 RedirectUri 由 SDK OIDC 事件设置 |
| `ResponseType = code` / `UsePkce = true` | 使用授权码与 PKCE(S256)；verifier 随 Data Protection 加密的 state 往返，多实例必须共享密钥环；授权码兑换、ID Token 协议校验由框架执行 |
| `ResponseMode = query` | Provider 通过顶层 GET 返回当前回调，匹配 Lax Cookie 策略 |
| `SaveTokens` | 将令牌保存在受保护的认证票据，供 BFF 刷新、内省与撤销 |
| `MapInboundClaims = false` | 保留 OIDC 原始 claim 名称，应用再统一转换 Principal |
| `DisableTelemetry` | 不向 Provider 附带组件版本遥测参数 |
| `PushedAuthorizationBehavior = Disable` | 保留普通授权请求方式，本次不启用可选的 PAR；不是所有 OIDC 客户端都必须禁用 PAR |
| `Scope.Clear()` 和添加 scope | 使用本应用校验后的 scope；规范化时补入业务 Audience，避免重复 scope |
| `ValidIssuer` | ID Token 签发者必须匹配配置的 Authority |
| `ValidAudience` | ID Token 的受众是当前 ClientId；不是 access token 的业务 API Audience |
| `ValidateIssuerSigningKey` / `ValidAlgorithms` | 校验签名密钥并限制 RS256；事件还保留当前应用要求 ID Token 必须带签名的规则 |
| `ClockSkew` | 允许 30 秒服务器时钟偏差 |
| `NameClaimType` | 使用 `name` 作为认证身份的名称字段 |
| `Backchannel` | 后台发现、JWKS、授权码兑换共用受控客户端与超时；Basic 只用于 POST 表单 |
| `CorrelationCookie` / `NonceCookie` | 框架分别用于登录往返关联和 ID Token 重放防护；当前 GET 回调采用 Lax，并遵从本应用 Secure 策略 |
| `RemoteAuthenticationTimeout` | 与额外流程存储的 Lifetime 保持一致，避免两层登录状态有效期不一致 |

部分赋值显式重申了框架默认值，例如 PKCE 和 HttpOnly。这是便于审阅当前安全策略，不代表应用重新实现了对应协议。代码中的中文注释与本表相对应。

复用接线示例见 [Extension README](../admin/src/NexusAuth.Extension/README.md)。
