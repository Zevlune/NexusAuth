# Workbench API：Cookie + OIDC BFF 接入模板

这是仓库中 Workbench API 的服务端 BFF 模板，适合由服务端保存 OAuth 凭据、浏览器只持有本地会话 Cookie 的 .NET 应用。它只使用 ASP.NET Core 标准 `AddOpenIdConnect` 和 Cookie 认证。

实现见 [`admin/src/NexusAuth.Workbench.Api`](../admin/src/NexusAuth.Workbench.Api)。整套管理工作台的部署与操作见 [Workbench 使用手册](Workbench使用手册.md)；协议字段与验证规则见 [Host 使用手册](NexusAuth.Host使用手册.md) 和 [协议设计](11-OAuth-OIDC协议设计.md)。

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

## 登录链路

1. [`AuthController.Login`](../admin/src/NexusAuth.Workbench.Api/Controllers/AuthController.cs) 先在流程状态存储中登记本次登录，再调用标准 OIDC challenge。微软 `AddOpenIdConnect` 生成 state、nonce 和 PKCE verifier/challenge，返回 `authorizeUrl` 给前端。
2. 浏览器访问 Provider 的授权端点。用户完成登录后，Provider 按登记的 `redirect_uri` 回到 `/signin-oidc`。
3. 标准 OIDC handler 消费 correlation/state、PKCE 和 nonce，兑换授权码，并从 Discovery 获取 JWKS，校验 ID Token 的签名、issuer、audience、时效和 nonce。
4. 成功后，标准 handler 把令牌写入受保护的 `.NexusAuth.Workbench` HttpOnly Cookie，并跳转 Dashboard 的 `/auth/callback`。浏览器 JavaScript 无法读取 Cookie 中的明文令牌。

Workbench 为 OIDC 后通道配置专用 `HttpClient` delegating handler。该 handler 只把 token 请求中的客户端凭据改为 `Authorization: Basic ...` 请求头，以匹配 Bootstrap 登记的 `client_secret_basic`。它不调用 `HandleCodeRedemption`，也不重复执行 `ProtocolValidator`。

内部 [`IWorkbenchTokenClient`](../admin/src/NexusAuth.Workbench.Api/Authentication/Tokens/IWorkbenchTokenClient.cs) 只提供 refresh、introspection 和 revoke，并复用标准 `OpenIdConnectOptions.ConfigurationManager` 中缓存的发现文档。它不负责登录端点、PKCE 或 ID Token 校验。

## 流程状态

[`NexusAuth.Extension`](../admin/src/NexusAuth.Extension) 提供可复用的 OIDC 流程状态、Basic 后台请求适配及原生元数据预热，与标准 `AddOpenIdConnect` 组合使用：

```csharp
services.AddOpenIdConnectFlowState()
    .UseInMemory(options =>
    {
        options.Lifetime = TimeSpan.FromMinutes(10);
        options.Capacity = 10_000;
    });
```

多实例使用 Redis，或其他存储通过 `UseFlowStateStore<T>()` 注册：

```csharp
services.AddOpenIdConnectFlowState()
    .UseRedis(options => configuration.GetSection("Auth:FlowState:Redis").Bind(options));
```

扩展不依赖 `NexusAuth.Shared` 或 `NexusAuth.Workbench`，不提供登录端点，不生成 PKCE，也不接管 ID Token 校验。它保留现有 TTL、容量、限流、安全、会话撤销和 JSON 持久化契约。多副本还必须共享并持久化 ASP.NET Core Data Protection key ring，否则 correlation、nonce 和会话 Cookie 无法跨实例解密。

## 验证请求与续期

管理请求统一使用 `.NexusAuth.Workbench` Cookie；Workbench 不注册 JWT Bearer 认证方案，也不接受外部 access token。[`WorkbenchCookieAuthenticationEvents`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchCookieAuthenticationEvents.cs) 在 Cookie 验证时检查保存的 access token：

- 距离过期超过一分钟时，通过 introspection 确认令牌仍有效，且 subject、client_id 与当前会话匹配；
- 接近过期时使用 `IWorkbenchTokenClient` 刷新，再对新 token 执行 introspection；
- 成功后保存轮换后的 access/refresh token，并续到新的 24 小时本地会话窗口；
- 任一步失败时拒绝 Principal 并清除 Cookie。

Provider 登录页的“几天内免登录”和 Workbench 的 24 小时 BFF 会话是两层配置，互不相同。

## 退出

`POST /api/auth/logout` 在最长 5 秒的 best-effort 窗口内尝试撤销 refresh token 和 access token，并始终清除本地 Cookie。若 `Auth:SignOutProvider=true` 且存在 ID Token，API 通过标准 OIDC sign-out 获取 `end_session_endpoint` 地址，返回前端所需的 `logoutUrl`。Provider 不可用时仍完成本地退出，但无法保证远端令牌已撤销。

## 对照实现

- [`AuthController`](../admin/src/NexusAuth.Workbench.Api/Controllers/AuthController.cs)：发起标准 OIDC challenge、返回登录地址和执行本地登出。
- [`WorkbenchAuthenticationExtensions`](../admin/src/NexusAuth.Workbench.Api/Authentication/WorkbenchAuthenticationExtensions.cs)：Cookie、OpenID Connect、流程状态和 Basic 后通道配置。
- [`UseOpenIdConnectClientSecretBasic`](../admin/src/NexusAuth.Extension/Authentication/OpenIdConnectBackchannelExtensions.cs)：为指定原生 OIDC 方案适配后台表单请求的 Basic 认证头。
- [`WorkbenchTokenClient`](../admin/src/NexusAuth.Workbench.Api/Authentication/Tokens/WorkbenchTokenClient.cs)：refresh、introspection 和 revoke。
- [`WorkbenchCookieAuthenticationEvents`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchCookieAuthenticationEvents.cs)：会话 introspection 与 refresh token 轮换。
- [`CurrentIdentityController`](../admin/src/NexusAuth.Workbench.Api/Controllers/CurrentIdentityController.cs)：`GET /api/auth/me`。

`Auth:Authority` 必须与 Provider 的 issuer 完全一致，并且浏览器和 Workbench 进程都能直接访问。服务端位于容器内时，`localhost` 指向该容器自身；请让同一域名或 IP 通过 DNS 和网络路由同时可达 Provider。默认值和运行示例见 [Workbench 使用手册](Workbench使用手册.md)。
