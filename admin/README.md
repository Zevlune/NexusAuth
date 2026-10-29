# NexusAuth Workbench（管理 API + Dashboard）

本目录是 NexusAuth Workbench 的源码根，对应解决方案 `admin/NexusAuth.Admin.sln`。Workbench 是一套独立于认证服务器的管理工作台，不是认证服务本身。

- **Workbench API**：`admin/src/NexusAuth.Workbench.Api`，固定使用 ASP.NET Core OpenID Connect + Cookie 的 BFF 登录，另含管理 API 与 Swagger。
- **Dashboard**：`admin/src/NexusAuth.Workbench.Dashboard`，React 管理界面，见 [Dashboard README](./src/NexusAuth.Workbench.Dashboard/README.md)。
- **OIDC 扩展**：`admin/src/NexusAuth.Extension`，与标准 ASP.NET Core `AddOpenIdConnect` 组合使用的流程状态存储、Basic 后台请求适配与元数据预热，见 [Extension README](./src/NexusAuth.Extension/README.md)。

两套应用的分工：**Workbench API + Dashboard 是一套管理工作台；[NexusAuth.Host](../src/NexusAuth.Host) 是独立的身份认证服务器。** Host 提供登录、授权、令牌和身份协议，可单独运行；Workbench 通过 OIDC 委托 Host 完成人员登录，再管理用户、客户端、资源、凭证和审计。当前实现下两者共用同一个 `nexusauth` 数据库和 schema。

完整的部署、配置、操作与排错步骤见 **[Workbench 使用手册](../document/Workbench使用手册.md)**；认证服务本身的启动与配置见 [NexusAuth.Host 使用手册](../document/NexusAuth.Host使用手册.md)。

## 组件与地址

| 组件 | 项目路径 | 源码监听 | Compose 对外 |
| --- | --- | --- | --- |
| Workbench API | `admin/src/NexusAuth.Workbench.Api` | `http://localhost:5051` | 不映射宿主端口，仅容器内 `admin-api:8080` |
| Dashboard | `admin/src/NexusAuth.Workbench.Dashboard` | `http://localhost:5273`（Vite） | `http://localhost:5560` |
| NexusAuth.Host | `src/NexusAuth.Host` | `http://localhost:5100` | `http://localhost:5100` |

生产环境应让浏览器只访问 Dashboard 域名，由反向代理把 `/api` 和 `/signin-oidc` 转发到 Workbench API。客户端密钥留在服务端；access token 和 refresh token 位于 API 签发的加密 HttpOnly 票据 Cookie 中，浏览器 JavaScript 读不到明文。

## 运行前提

- .NET 10 SDK；
- Node.js 22 与 npm（运行 Dashboard）；
- PostgreSQL 16，或根目录 `docker-compose.yml` 的 `db`（宿主端口 `33210`）；
- 已初始化共享的 `nexusauth` 数据库与 schema：创建数据库由 `docker/postgres-init.sh` 负责（或手工先建库），`production-init.sql` 只建 schema 及表、索引；
- 一个可用的 NexusAuth.Host，启动前提见 [Workbench 使用手册](../document/Workbench使用手册.md)；
- 为 Workbench API 准备完整的 `appsettings` 配置，它会自行登记客户端和资源。

Host 默认关闭 SMTP。需要邮件功能时，在 Host 的 appsettings 中启用 `Smtp:Enabled` 并填写完整配置；详见 [SMTP 配置与发信验证](../document/SMTP发信试验.md)。Workbench API 的邮件配置独立管理。

## 启动方式总览

本地 Compose 预览必须同时叠加基础编排和本地覆盖。基础文件保留 HTTPS 安全默认，本地覆盖显式提供隔离演示所需的 HTTP 设置：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

对外地址：Dashboard `http://localhost:5560`，Host `http://localhost:5100`，数据库 `127.0.0.1:33210`；`admin-api` 不映射宿主端口。

源码方式需要多个终端分别启动数据库、Host、Workbench API 和 Dashboard。请按 [Workbench 使用手册第 4 节](../document/Workbench使用手册.md#4-从源码本地运行) 执行完整步骤。`appsettings.Development.json` 已提供本地数据库、Authority 和回调地址，但 `Auth:ClientSecret` 留空，首次运行前需要在本地文件中填写；不要提交真实密钥。

源码方式的安全默认如下：

- 基础 `appsettings.json` 包含完整配置结构，`Auth:Authority` 和 `Auth:ClientSecret` 为空，`Auth:RequireHttpsMetadata=true`；`appsettings.Development.json` 为本机开发提供 `http://localhost:5100`、`false`、本地数据库和 `http://localhost:5273` 回调。
- 基础 `Security:CookieSecurePolicy=Always`；Development 显式覆盖为 `SameAsRequest`。
- Swagger 只在 Development 环境启用；其他环境需要显式设置 `Swagger:Enabled=true`。
- `admin/src/NexusAuth.Workbench.Dashboard/vite.config.ts` 仍把 `/api`、`/signin-oidc` 代理到仓库中的局域网示例地址，本地应改为 API 地址（如 `http://localhost:5051`）。

`admin/src/NexusAuth.Workbench.Api/appsettings.Development.json` 已内置共享数据库连接串，端口就是 Compose 的 `33210`，库为 `nexusauth`，schema 为 `nexusauth`。

## Workbench API 配置

Workbench API 的业务配置统一放在 `appsettings.json` 和按 `ASPNETCORE_ENVIRONMENT` 选择的 `appsettings.{Environment}.json`。基础文件提供安全默认值和完整配置结构，环境文件覆盖部署差异。应用内各功能仍通过 `IOptions<T>` 读取配置。

| 配置分区 | 主要键 | 用途 |
| --- | --- | --- |
| `ConnectionStrings` | `Default` | PostgreSQL 连接串；Compose 和生产文件使用占位密码或私有值 |
| `Auth` | `Authority`、`ClientId`、`ClientSecret`、`RedirectUri`、`PostLogoutRedirectUri`、`Audience`、`Scope` | OIDC 登录、BFF 会话与 Bootstrap 客户端 |
| `Auth:FlowState` | `Provider`、`Lifetime`、`Capacity`、`Redis` | 登录流程状态；多实例使用 Redis |
| `Security` | `CookieSecurePolicy`、`ForwardedHeadersEnabled`、`ForwardLimit`、`KnownProxies`、`KnownNetworks` | Cookie 与可信代理 |
| `Swagger` | `Enabled` | 非 Development 环境是否开放 Swagger |
| `Bootstrap` | `ResourceName`、`ResourceDisplayName`、`ClientName` 等 | Workbench 启动时登记自身的资源和客户端 |
| `Smtp` | `Enabled`、`Host`、`Port`、`Security`、凭据与发件人 | Workbench 邮件功能；默认关闭 |

根 `docker-compose.yml` 只向 `admin-api` 注入 `ASPNETCORE_ENVIRONMENT` 和 `TZ`，业务值全部来自挂载文件。默认把 `docker/workbench.appsettings.json` 挂到 `/app/appsettings.${ASPNETCORE_ENVIRONMENT:-Production}.json`；`NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE` 只用于替换这一个文件路径。生产建议从该模板复制出已被 gitignore 的 `docker/workbench.private.appsettings.json`，填写后把变量指向私有文件。本地覆盖文件切换到 `docker/workbench.local.appsettings.json`。

独立部署示例位于 `deploy/direct/`：复制 `workbench.appsettings.example.json` 为已忽略的 `workbench.appsettings.json`，Compose 会只读挂到 `/app/appsettings.Production.json`。该示例的 `Auth:Authority=https://sso.example.com`，`Auth:ClientSecret` 留空；基础 `appsettings.json` 已提供 Bootstrap 默认元数据，不需要再由 `launchSettings.json` 注入。

框架原生环境变量和命令行配置仍可用，但它们不再作为主要部署流程，项目也不再提供 `NEXUSAUTH_WORKBENCH_*` 单下划线别名。完整配置项、样例文件和覆盖顺序见 [Workbench 使用手册第 5 节](../document/Workbench使用手册.md#5-workbench-api-配置)。

### 单一 Authority

`Auth:Authority` 是 Workbench 唯一使用的 Provider 基础地址。微软 OIDC handler 用它完成 Discovery、JWKS 获取、授权跳转、授权码兑换和 ID Token 校验；Workbench 内部 `IWorkbenchTokenClient` 也从同一发现文档读取 refresh、introspection 和 revoke 端点。它必须与 Provider 的 issuer 完全一致。基础配置为空且 `Auth:RequireHttpsMetadata=true`；其他环境必须使用 HTTPS Authority。

Workbench 配置通过 [`IOptions<WorkbenchAuthenticationOptions>`](./src/NexusAuth.Workbench.Api/Options/WorkbenchAuthenticationOptions.cs)、`IOptions<WorkbenchSecurityOptions>`、`IOptions<WorkbenchSwaggerOptions>` 和 `IOptions<WorkbenchBootstrapOptions>` 获取。认证选项统一绑定、规范化并在启动时校验，不直接注册配置对象单例。`IWorkbenchTokenClient` 仅处理刷新、内省和撤销；发现文档由标准 OIDC handler 的配置管理器共享缓存，没有额外的发现服务注册。目录职责见 [Workbench API README](./src/NexusAuth.Workbench.Api/README.md)。


浏览器和 Workbench API 进程都必须能直接访问同一个 `Auth:Authority`。容器内的 `localhost` 指向容器自身；跨容器部署应使用浏览器和各工作负载都能访问的同一域名或 IP，并通过网络 DNS 或入口路由把它解析到 Provider。不要把 `Auth:Authority` 改成容器服务名，也不要再额外配置内网地址或 HTTP 地址改写。

### 可信代理与 Swagger

Workbench 默认不处理转发头。反向代理部署时设置 `Security:ForwardedHeadersEnabled=true`，并至少提供一个明确的 `Security:KnownProxies` IP 或 `Security:KnownNetworks` CIDR；两项都为空会阻止启动。`KnownProxies`、`KnownNetworks` 支持逗号或分号分隔，`Security:ForwardLimit` 默认 1、只允许 1 到 10。不要信任整个公网或包含普通客户端的宽泛网段，应用端口也应只对可信代理开放。

Swagger 由 `Swagger:Enabled` 控制：未设置时只在 Development 开启，其他环境默认关闭；只有显式设置为 `true` 才会在非 Development 环境注册 `/swagger`。

### 自身 Bootstrap

Workbench API 启动时幂等登记自己的 OAuth 客户端和 API resource（见 [`WorkbenchClientCredentialHostedService`](./src/NexusAuth.Workbench.Api/Bootstrap/WorkbenchClientCredentialHostedService.cs)）：客户端固定使用 `client_secret_basic`、PKCE 必需、grant 为 `authorization_code` + `refresh_token`，scope 为 `openid profile offline_access` 加 `Auth:Audience`；`Auth:Audience` 同时作为 API resource 的 audience。若数据库密钥与 `Auth:ClientSecret` 不一致会按配置同步。`openid`/`profile`/`offline_access` 是静态 scope，不会建成资源。

## 管理接口与权限

管理接口需要 Workbench Cookie。未登录返回 `401`，已登录但没有管理访问权限返回 `403`。**并非所有接口都是系统管理员专属**：

第一档，任何已登录用户可访问（控制器只标 `[Authorize]`）：

| 路由 | 能力 |
| --- | --- |
| `/api/clients` | 应用查询、创建、更新、凭据生成/重置、删除 |
| `/api/api-resources` | 服务资源查询、创建、更新、启停、删除 |
| `/api/client-metadata` | 客户端元数据查询 |
| `/api/login-audits` | 登录审计查询 |
| `/api/scim-credentials` | SCIM 凭证查询、创建、更新、撤销 |
| `/api/open-api-credentials` | 开放 API 凭证查询、创建、更新、撤销 |

第二档，额外要求操作者是启用的系统账号（`IsActive=true` 且 `IsSystemAccount=true`），否则 403：

| 路由 | 能力 |
| --- | --- |
| `/api/users` | 用户查询、创建、资料更新、启停、重置密码、删除、吊销 token |
| `/api/users/{id}/passkeys` | Passkey 查询与撤销 |

认证生命周期端点：`GET /api/auth/config`、`GET /api/auth/login`、`GET /signin-oidc`、`GET /api/auth/me` 为匿名；`POST /api/auth/logout` 需登录。`/api/users` 与门禁相关的控制器直接返回 HTTP 状态，避免结果包装把 403/删除结果变成 200；其余管理控制器使用统一 API 结果包装。接口细节以 Swagger 为准。

## 登录、会话与登出

1. Dashboard 请求 `GET /api/auth/me` 检查 Cookie；未登录则走登录页。
2. `GET /api/auth/login` 返回前端所需的 JSON `authorizeUrl`。微软 OIDC handler 负责 state、nonce、PKCE、回调处理以及 ID Token 的签名、issuer、audience 和时效校验。
3. Workbench 流程状态默认 10 分钟过期、最多 10,000 个；登录发起端点按客户端 IP 限流。容量或频率超限返回 `429`。
4. 启动时后台服务预热 Discovery；失败只记录日志并按有限延迟重试，不阻塞应用启动。运行时元数据与 JWKS 由单例配置管理器刷新。
5. Provider 回调 `GET /signin-oidc` 后，OIDC handler 完成标准校验，API 写入 `.NexusAuth.Workbench` HttpOnly Cookie，并跳转前端 `/auth/callback`。
6. `/api` 与 `/signin-oidc` 响应使用 `Cache-Control: no-store` 和 `Pragma: no-cache`。
7. Cookie 默认 `SecurePolicy=Always`；Development 为本地 HTTP 显式使用 `SameAsRequest`。Cookie 验证在 access token 距过期超过 1 分钟时执行 introspection，接近过期时用 refresh token 刷新并复检，随后续到新的 24 小时窗口；失败则拒绝并清 Cookie。
8. `POST /api/auth/logout` 使用最长 5 秒的 best-effort 窗口撤销 refresh token 和 access token；`SignOutProvider=true` 时从 Discovery 获取 endsession 端点并返回带 `id_token_hint`、`post_logout_redirect_uri` 的 `logoutUrl`。无论撤销或 Discovery 是否失败，最终都会清除本地 Cookie。
9. 标准 OIDC handler 负责授权码兑换、nonce 与 ID Token 校验。Workbench 为 token 请求配置专用 `HttpClient` delegating handler，以 `client_secret_basic` 写入认证头；它不接管 `HandleCodeRedemption`，也不重复运行 `ProtocolValidator`。

管理 API 的结果过滤器保留原始 HTTP 状态和 `204 No Content`；未知异常记录日志后返回固定文案的 HTTP 500，不把异常详情返回客户端。

Workbench 使用 `NexusAuth.Extension` 的流程状态扩展：`services.AddOpenIdConnectFlowState().UseInMemory()` 为默认方式，`UseRedis(...)` 用于多实例共享。配置位于 `Auth:FlowState`；Redis 连接、KeyPrefix 和 Database 位于 `Auth:FlowState:Redis`。Redis 实现继续保证浏览器绑定、原子一次消费和跨实例共享容量。多实例部署还必须共享 ASP.NET Core Data Protection key ring。

## 构建与部署

```bash
dotnet build admin/NexusAuth.Admin.sln
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run build
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run lint
```

Dockerfile：API 为 `admin/src/NexusAuth.Workbench.Api/Dockerfile`（SDK 10 构建，监听 8080），Dashboard 为 `admin/src/NexusAuth.Workbench.Dashboard/Dockerfile`（Node 22 构建，Nginx 1.27 托管，监听 80）。根 Compose 映射 Dashboard 到 `5560`、数据库到 `33210`、Host 到 `5100`，`admin-api` 不映射宿主端口。本地预览用 `docker-compose.yml` 加 `docker-compose.local.yml`，由后者显式开启 HTTP 覆盖；生产使用基础 HTTPS 默认，配置明确可信代理、共享 Data Protection key ring 和密钥管理，不要使用仓库内置开发密钥。

## 相关文档

- [Workbench 使用手册](../document/Workbench使用手册.md)（完整部署、配置、操作与排错）
- [NexusAuth.Host 使用手册](../document/NexusAuth.Host使用手册.md)
- [Dashboard README](./src/NexusAuth.Workbench.Dashboard/README.md)
- [Workbench API 统一登录接入模板](../document/Workbench-API统一登录接入模板.md)
- [Workbench Dashboard 统一登录接入模板](../document/Workbench-Dashboard统一登录接入模板.md)
- [OAuth/OIDC 协议设计](../document/11-OAuth-OIDC协议设计.md)
- [Workbench API 源码目录导览](./src/NexusAuth.Workbench.Api/README.md)

认证注册各项配置的用途和复用边界见 [Workbench 认证注册说明](../document/Workbench认证注册说明.md)。
