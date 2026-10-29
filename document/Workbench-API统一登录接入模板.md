# Workbench API：Cookie BFF + JWT Bearer 接入模板

这是仓库中 Workbench API 的服务端 BFF 模板，适合由服务端保存 OAuth 凭据、浏览器只持有本地会话 Cookie 的 .NET 应用，也支持无头客户端直接发送 JWT Bearer。它使用 ASP.NET Core 标准 `AddOpenIdConnect`、Cookie 和 `AddJwtBearer`；默认方案 `Workbench` 在没有 `Authorization` 头时使用 Cookie，出现任意 `Authorization` 头时只使用 Bearer。

实现见 [`admin/src/NexusAuth.Workbench.Api`](../admin/src/NexusAuth.Workbench.Api)。整套管理工作台的部署与操作见 [Workbench 使用手册](Workbench使用手册.md)；协议字段与验证规则见 [Host 使用手册](NexusAuth.Host使用手册.md) 和 [协议设计](11-OAuth-OIDC协议设计.md)。

## 与 main 的 SSO 接入对照

对照基准为远端 `main` 的 `e8ffeb3`。Workbench 保持一个真实的 SSO 客户端示例；精简的目标是把协议工作交给框架，同时保留可运行的登录、API 访问和会话生命周期。

| 接入环节 | main 的实现 | 当前实现及保留的行为 |
| --- | --- | --- |
| 发起登录 | 自定义 SDK 拼接授权地址、生成 PKCE | `ChallengeAsync` 调用原生 OIDC；仍通过 `GET /api/auth/login` 返回 `{ authorizeUrl }` |
| 发现文档和签名密钥 | 自定义 SDK 获取和使用元数据 | OIDC 的 `ConfigurationManager` 负责缓存与刷新，Bearer 共用；预热仅用于提前加载 |
| 登录回调 | 控制器 `/signin-oidc` 自行兑换 code、校验 ID Token | 同一路径由 OIDC 中间件处理；成功仍写 Cookie 并跳转前端 `/auth/callback` |
| Dashboard 会话 | HttpOnly Cookie，保存令牌并续期 | 保留 Cookie、刷新、内省及本地会话策略；前端不持有明文 token |
| 无头 API 调用 | JWT Bearer | 保留 `AddJwtBearer`，与 Cookie 并存；显式传入 Authorization 时只验证 Bearer |
| 退出 | 清 Cookie 并返回 `{ logoutUrl }` | 保留接口契约，补充令牌撤销；Provider 退出仍由浏览器访问返回地址并确认 |

没有另加一套授权码客户端或 PKCE 实现。下面的配置、事件与接口就是 Workbench 当前运行的接入示例。

## 复制到其他应用时的边界

| 部分 | 如何参考 |
| --- | --- |
| 标准认证注册 | 对照 `WorkbenchAuthenticationExtensions` 配置 Cookie、OIDC；需要接收 access token 的 API 再注册 Bearer 和对应 audience |
| 前后端接口契约 | 参考 `AuthController`、`CurrentIdentityController` 以及 Dashboard 的登录/落地页；回调由中间件处理 |
| Extension | 按需复用 Basic 适配、额外的一次性流程存储和元数据预热；它们不替代标准认证中间件 |
| 刷新与 Cookie 事件 | 当前示例包含 24 小时滑动会话、提前一分钟刷新、每次内省等应用策略；按新应用的会话要求选择 |
| Workbench Bootstrap | 自动创建自身客户端和资源是管理台的部署便利；普通 SSO 客户端在 Provider 登记好客户端即可，不需要接入 Provider 数据库 |
| Workbench 授权和撤销校验 | 系统管理员策略及 `IUserTokenValidator` 的直接查库是本应用业务规则；新应用应实现自己的授权策略，不复制 Workbench 的数据库和领域服务依赖 |

迁移时先替换客户端 ID、密钥、API audience/scope 和公开回调地址，再接通登录与 `/api/auth/me`，最后按需加入刷新、撤销和多实例流程存储。不要把整个 `WorkbenchApiModule`、管理控制器或 Persistence 模块当作 SSO SDK 复制。

## 先登记客户端

在 NexusAuth 登记一个机密客户端和 API 服务资源。Workbench 自身由启动初始化器完成这一步；其他应用可通过管理界面登记，不应复用 `nexusauth.workbench` 或它的密钥。

| 项目 | 本例 | 新应用需要替换为 |
| --- | --- | --- |
| Client ID | `nexusauth.workbench` | 应用自己的稳定 ID |
| 认证方式 | `client_secret_basic` | Workbench 固定使用 Basic |
| Grant | `authorization_code`、`refresh_token` | Workbench 固定使用这两项 |
| PKCE | S256，必需 | 保持启用 |
| Scope | `openid profile offline_access nexusauth.workbench.api` | 身份 scope 与目标 API scope |
| Audience | `nexusauth.workbench.api` | 目标 API 的 audience |
| 回调 | `/signin-oidc` | 服务端实际处理回调的公开 URL |
| 登出回调 | Dashboard 根地址 | 已登记且逐字符匹配的 URL |

Workbench API 从 `Auth:Authority` 读取 Provider 公开 Issuer，必须与 Discovery 和 ID Token 的 `iss` 一致。`Auth:ClientSecret` 只放在服务端的私有 `appsettings.{Environment}.json` 中。`Auth:Audience` 同时用于请求 API resource scope 和 Bootstrap resource audience。Compose 和独立部署都通过挂载 JSON 提供业务配置，`NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE` 仅选择 Compose 使用的文件路径；完整配置见 [Workbench 使用手册](Workbench使用手册.md#5-workbench-api-配置)。

Bearer access token 的 audience 必须是 `Auth:Audience`，OIDC ID token 的 audience 必须是 `Auth:ClientId`。调用方的 `client_id` 默认只允许 `Auth:ClientId`；其他已登记客户端要显式加入 `Auth:AllowedBearerClientIds`。Dashboard 不获取或转发 access token，仍使用 Cookie。

## 登录链路

1. [`AuthController.Login`](../admin/src/NexusAuth.Workbench.Api/Controllers/AuthController.cs) 先在流程状态存储中登记本次登录，再调用标准 OIDC challenge。微软 `AddOpenIdConnect` 生成 state、nonce 和 PKCE verifier/challenge，返回 `authorizeUrl` 给前端。
2. 浏览器访问 Provider 的授权端点。用户完成登录后，Provider 按登记的 `redirect_uri` 回到 `/signin-oidc`。
3. 标准 OIDC handler 消费 correlation/state、PKCE 和 nonce，兑换授权码，并从 Discovery 获取 JWKS，校验 ID Token 的签名、issuer、audience、时效和 nonce。
4. 成功后，标准 handler 把令牌写入受保护的 `.NexusAuth.Workbench` HttpOnly Cookie，并跳转 Dashboard 的 `/auth/callback`。浏览器 JavaScript 无法读取 Cookie 中的明文令牌。

Workbench 为 OIDC 后通道配置专用 `HttpClient` delegating handler。该 handler 只把 token 请求中的客户端凭据改为 `Authorization: Basic ...` 请求头，以匹配 Bootstrap 登记的 `client_secret_basic`。它不调用 `HandleCodeRedemption`，也不重复执行 `ProtocolValidator`。

内部 [`IWorkbenchTokenClient`](../admin/src/NexusAuth.Workbench.Api/Authentication/Tokens/IWorkbenchTokenClient.cs) 只提供 refresh、introspection 和 revoke，并复用标准 `OpenIdConnectOptions.ConfigurationManager` 中缓存的发现文档。它不负责登录端点、PKCE 或 ID Token 校验。

## 流程状态

[`NexusAuth.Extension`](../admin/src/NexusAuth.Extension) 提供可复用的 OIDC 流程状态、Scoped `OpenIdConnectFlowStateEvents`、Basic 后台请求适配及原生元数据预热，与标准 `AddOpenIdConnect` 组合使用：

```csharp
services.AddOpenIdConnectFlowState()
    .UseInMemory(options =>
    {
        options.Lifetime = TimeSpan.FromMinutes(10);
        options.Capacity = 10_000;
    });

services.AddAuthentication()
    .AddOpenIdConnect("oidc", options =>
    {
        options.EventsType = typeof(OpenIdConnectFlowStateEvents);
    });
```

多实例使用 Redis，或其他存储通过 `UseFlowStateStore<T>()` 注册：

```csharp
services.AddOpenIdConnectFlowState()
    .UseRedis(options => configuration.GetSection("Auth:FlowState:Redis").Bind(options));
```

`AddOpenIdConnectFlowState()` 自动 Scoped 注册 `OpenIdConnectFlowStateEvents`。内置事件在 `MessageReceived` 中原子消费流程并核对浏览器绑定；校验成功后调用 `base` 委托，校验失败则不执行后续用户委托。授权和 Provider 登出 redirect 只检查端点，不写应用 callback、post-logout callback、会话或错误页。自定义事件继承该类并覆盖相关方法时应调用 `base`，以保留检查。

应用仍须在 challenge 前调用 `BeginAsync`，处理失败时调用 `CancelAsync`。Workbench 使用继承类型 `WorkbenchOpenIdConnectEvents`，保留 24 小时会话、RS256 限制和前端错误跳转。Cookie 事件中的业务刷新、introspection 与撤销检查留在 Workbench，不进入 Extension。

扩展不依赖 `NexusAuth.Shared` 或 `NexusAuth.Workbench`，不提供登录端点，不生成 PKCE，不新增协议客户端，也不接管 ID Token 校验。多副本还必须共享并持久化 ASP.NET Core Data Protection key ring，否则 correlation、nonce 和会话 Cookie 无法跨实例解密。

## 请求认证

默认认证方案 `Workbench` 按请求选择 Cookie 或 Bearer：

- 没有 `Authorization` 头时使用 `.NexusAuth.Workbench` Cookie。
- 存在任意 `Authorization` 头时只使用 Bearer，返回 401 也不回退 Cookie。
- 受保护 API 的无效 Bearer 由框架返回 401 和 `WWW-Authenticate: Bearer`。`/api/auth/me` 是 `AllowAnonymous`，无效 Bearer 时由控制器返回 401，不附带该 challenge。

Bearer 由框架验证 RS256、issuer、`Auth:Audience`、签名和时效；随后 [`WorkbenchJwtBearerEvents`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchJwtBearerEvents.cs) 要求 `token_use=access_token`，允许 `client_id=Auth:ClientId` 或 `Auth:AllowedBearerClientIds` 中的值，再调用 `IUserTokenValidator` 检查用户禁用、会话撤销、token epoch 和 jti 黑名单。JWT Bearer 与 OIDC 登录共用同一个框架 metadata manager。OIDC ID token 的 audience 是 `Auth:ClientId`，不能用于 API。

无头调用示例：

```bash
curl -H "Authorization: Bearer $NEXUSAUTH_ACCESS_TOKEN" \
  http://localhost:5051/api/auth/me
```

Dashboard 仍只使用 Cookie，不读取、保存或转发 access token。无头客户端不需要会话 Cookie。

[`WorkbenchCookieAuthenticationEvents`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchCookieAuthenticationEvents.cs) 在 Cookie 验证时检查保存的 access token：

- 距离过期超过一分钟时，通过 introspection 确认令牌仍有效，且 subject、client_id 与当前会话匹配；
- 接近过期时使用 `IWorkbenchTokenClient` 刷新，再对新 token 执行 introspection；
- 成功后保存轮换后的 access/refresh token，并续到新的 24 小时本地会话窗口；
- 任一步失败时拒绝 Principal 并清除 Cookie。

Provider 登录页的“几天内免登录”和 Workbench 的 24 小时 BFF 会话是两层配置，互不相同。

## 退出

`POST /api/auth/logout` 按入口区分：

- Bearer 调用只请求 Provider revoke 本次保存的 access token，不清 Cookie、不发起 Provider 登出，并返回空 `logoutUrl`。revoke 失败只记录日志，接口仍返回 HTTP 200；成功响应不保证远端已完成撤销。
- Cookie 调用在最长 5 秒的 best-effort 窗口内请求撤销 refresh token 和 access token，并始终清除本地 Cookie。若 `Auth:SignOutProvider=true` 且存在 ID token，API 通过标准 OIDC sign-out 获取 `end_session_endpoint` 地址，返回前端所需的 `logoutUrl`。Provider 不可用时仍完成本地退出，但无法保证远端令牌已撤销。

Bearer logout 与 Cookie logout 都不会承诺跨客户端或 Provider 全局会话已经结束。

## 对照实现

- [`AuthController`](../admin/src/NexusAuth.Workbench.Api/Controllers/AuthController.cs)：发起标准 OIDC challenge、返回登录地址，并按 Cookie/Bearer 入口执行不同登出语义。
- [`WorkbenchAuthenticationExtensions`](../admin/src/NexusAuth.Workbench.Api/Authentication/WorkbenchAuthenticationExtensions.cs)：默认策略、Cookie、OpenID Connect、JWT Bearer、流程状态和 Basic 后通道配置。
- [`WorkbenchJwtBearerEvents`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchJwtBearerEvents.cs)：Bearer 的 `token_use`、客户端白名单和用户撤销检查。
- [`UseOpenIdConnectClientSecretBasic`](../admin/src/NexusAuth.Extension/Authentication/OpenIdConnectBackchannelExtensions.cs)：为指定原生 OIDC 方案适配后台表单请求的 Basic 认证头。
- [`WorkbenchTokenClient`](../admin/src/NexusAuth.Workbench.Api/Authentication/Tokens/WorkbenchTokenClient.cs)：refresh、introspection 和 revoke。
- [`WorkbenchCookieAuthenticationEvents`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchCookieAuthenticationEvents.cs)：会话 introspection 与 refresh token 轮换。
- [`CurrentIdentityController`](../admin/src/NexusAuth.Workbench.Api/Controllers/CurrentIdentityController.cs)：`GET /api/auth/me`。
- [`Models/Requests`](../admin/src/NexusAuth.Workbench.Api/Models/Requests)：控制器请求模型，每类型一文件。

`Auth:Authority` 必须与 Provider 的 issuer 完全一致，并且浏览器和 Workbench 进程都能直接访问。服务端位于容器内时，`localhost` 指向该容器自身；请让同一域名或 IP 通过 DNS 和网络路由同时可达 Provider。默认值和运行示例见 [Workbench 使用手册](Workbench使用手册.md)。

## 本地运行与验收

从源码运行时，默认地址为 Host `http://localhost:5100`、API `http://localhost:5051`、Dashboard `http://localhost:5273`。Dashboard 的 `/api` 和 `/signin-oidc` 代理均指向本机 API。运行前在环境 appsettings 中填写数据库连接和 `Auth:ClientSecret`；Workbench 会将自身客户端登记同步到同库的 Host。完整启动命令见 [从源码本地运行](Workbench使用手册.md#4-从源码本地运行)。

接入另一个 SSO 时，至少验证以下行为，不能只检查项目能否编译：

1. 未登录调用 `/api/auth/me` 返回 401；发起登录返回 `authorizeUrl`。
2. 完成 Provider 登录、授权确认和 `/signin-oidc` 回调后，前端落地页能获取当前身份。
3. Cookie 能访问业务 API；独立获取的 Bearer access token 也能按其授权访问，错误 Bearer 不回退到 Cookie。
4. access token 接近过期后，Cookie 会话能刷新令牌并继续访问。
5. Cookie 退出清除本地会话；访问返回的 Provider 登出地址并确认后，Provider 会话也结束。

2026-10-29 已用独立 PostgreSQL、真实 Host/API 进程与 Vite 代理完成上述 HTTP 流程，同时验证 Bearer 撤销、管理列表、参数错误 400 和资源重复错误 409。该次验证使用源码进程部署；未将结果等同于生产域名、HTTPS 或 Compose 网络验证。
