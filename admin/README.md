# NexusAuth Workbench（管理 API + Dashboard）

本目录是 NexusAuth Workbench 的源码根，对应解决方案 `admin/NexusAuth.Admin.sln`。Workbench 是一套独立于认证服务器的管理工作台，不是认证服务本身。

- **Workbench API**：`admin/src/NexusAuth.Workbench.Api`，OIDC BFF（登录、回调、Cookie、刷新、登出）+ 管理 API + Swagger。
- **Dashboard**：`admin/src/NexusAuth.Workbench.Dashboard`，React 管理界面，见 [Dashboard README](./src/NexusAuth.Workbench.Dashboard/README.md)。
- **接入扩展**：`admin/src/NexusAuth.Extension`，可被其他 .NET 应用复用的 OIDC/PKCE/刷新/introspection 客户端实现。

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
- 一个可用的 NexusAuth.Host（BFF 模式必需），启动前提见 [Workbench 使用手册](../document/Workbench使用手册.md)；
- 为 Workbench API 配置完整的认证与 Bootstrap 环境变量，它会自行登记客户端和资源。

Host 默认关闭 SMTP。需要邮件功能时，在 Host 的 appsettings 中启用 `Smtp:Enabled` 并填写完整配置；详见 [SMTP 配置与发信验证](../document/SMTP发信试验.md)。Workbench API 的邮件配置独立管理。

## 启动方式总览

本地 Compose 预览必须同时叠加基础编排和本地覆盖。基础文件保留 HTTPS 安全默认，本地覆盖显式提供隔离演示所需的 HTTP 设置：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

对外地址：Dashboard `http://localhost:5560`，Host `http://localhost:5100`，数据库 `127.0.0.1:33210`；`admin-api` 不映射宿主端口。

源码方式需要多个终端分别启动数据库、Host、Workbench API 和 Dashboard。请按 [Workbench 使用手册第 4 节](../document/Workbench使用手册.md#4-从源码本地运行) 执行完整命令，其中包含 Host 初始管理员、Workbench 客户端密钥和回调地址；直接运行项目而不补齐配置会启动失败。

源码方式的安全默认如下：

- 基础 `appsettings.json` 的 `Auth:Authority` 为空，`Auth:RequireHttpsMetadata=true`；`appsettings.Development.json` 仅为本机开发提供 `http://localhost:5100` 和 `false`。
- 基础 `Security:CookieSecurePolicy=Always`；Development 显式覆盖为 `SameAsRequest`。
- Swagger 只在 Development 环境启用；其他环境需要显式设置 `Swagger:Enabled=true`。
- `admin/src/NexusAuth.Workbench.Dashboard/vite.config.ts` 仍把 `/api`、`/signin-oidc` 代理到仓库中的局域网示例地址，本地应改为 API 地址（如 `http://localhost:5051`）。

`admin/src/NexusAuth.Workbench.Api/appsettings.Development.json` 已内置共享数据库连接串，端口就是 Compose 的 `33210`，库为 `nexusauth`，schema 为 `nexusauth`。

## Workbench API 配置

核心配置在 `admin/src/NexusAuth.Workbench.Api/appsettings.json` 的 `Auth` 段，以及 `Bootstrap` 段。标准 .NET 双下划线环境变量可以直接覆盖配置；`WorkbenchConfigurationExtensions.cs` 另外显式映射了一组 `NEXUSAUTH_WORKBENCH_`（**单下划线**）便利变量。两种写法都可以：

| 环境变量 | 配置键 | 必填 |
| --- | --- | --- |
| `NEXUSAUTH_WORKBENCH_CONNECTION_STRINGS_DEFAULT` | `ConnectionStrings:Default` | 否 |
| `NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY` | `Auth:Authority` | 是（基础配置为空） |
| `NEXUSAUTH_WORKBENCH_AUTH_AUDIENCE` | `Auth:Audience` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_CLIENT_ID` | `Auth:ClientId` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_CLIENT_SECRET` | `Auth:ClientSecret` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_REDIRECT_URI` | `Auth:RedirectUri` | 非网关模式必填 |
| `NEXUSAUTH_WORKBENCH_AUTH_POST_LOGOUT_REDIRECT_URI` | `Auth:PostLogoutRedirectUri` | 非网关模式必填 |
| `NEXUSAUTH_WORKBENCH_AUTH_SCOPE` | `Auth:Scope` | 非网关模式必填 |
| `NEXUSAUTH_WORKBENCH_AUTH_REQUIRE_HTTPS_METADATA` | `Auth:RequireHttpsMetadata` | 否 |
| `NEXUSAUTH_WORKBENCH_AUTH_SIGN_OUT_PROVIDER` | `Auth:SignOutProvider` | 否 |
| `NEXUSAUTH_WORKBENCH_SECURITY_COOKIE_SECURE_POLICY` | `Security:CookieSecurePolicy` | 否 |
| `NEXUSAUTH_WORKBENCH_SECURITY_FORWARDED_HEADERS_ENABLED` | `Security:ForwardedHeadersEnabled` | 否 |
| `NEXUSAUTH_WORKBENCH_SECURITY_FORWARD_LIMIT` | `Security:ForwardLimit` | 否 |
| `NEXUSAUTH_WORKBENCH_SECURITY_KNOWN_PROXIES` | `Security:KnownProxies` | 启用转发头时二选一 |
| `NEXUSAUTH_WORKBENCH_SECURITY_KNOWN_NETWORKS` | `Security:KnownNetworks` | 启用转发头时二选一 |
| `NEXUSAUTH_WORKBENCH_SWAGGER_ENABLED` | `Swagger:Enabled` | 否 |
| `NEXUSAUTH_WORKBENCH_GATEWAY_ENABLED` | `Gateway:Enabled` | 否 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_NAME` | `Bootstrap:ResourceName` | 是 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DISPLAY_NAME` | `Bootstrap:ResourceDisplayName` | 是 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DESCRIPTION` | `Bootstrap:ResourceDescription` | 否 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_NAME` | `Bootstrap:ClientName` | 是 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_DESCRIPTION` | `Bootstrap:ClientDescription` | 否 |

这些单下划线变量的完整映射以 `admin/src/NexusAuth.Workbench.Api/WorkbenchConfigurationExtensions.cs` 为准。未被表覆盖的键可用标准 .NET 形式（如 `Auth__Xxx`）或由编排直接注入。完整说明见 [Workbench 使用手册第 5 节](../document/Workbench使用手册.md#5-workbench-api-配置)。

### 单一 Authority

`Auth:Authority` 是 Workbench 唯一使用的 Provider 基础地址，负责浏览器授权跳转、Discovery，以及令牌、JWKS、introspection 和 JWT issuer 校验。它必须与 Host 的 `Jwt:Issuer` 完全一致。基础配置为空且 `Auth:RequireHttpsMetadata=true`。配置层允许任何环境显式设为 `false`，但规范上仅限隔离本地开发显式关闭；其他环境必须使用 HTTPS Authority。

浏览器和 Workbench API 进程都必须能直接访问同一个 `Auth:Authority`。容器内的 `localhost` 指向容器自身；跨容器部署应使用浏览器和各工作负载都能访问的同一域名或 IP，并通过网络 DNS 或入口路由把它解析到 Provider。不要把 `Auth:Authority` 改成容器服务名，也不要再额外配置内网地址或 HTTP 地址改写。

### 可信代理与 Swagger

Workbench 默认不处理转发头。反向代理部署时设置 `Security:ForwardedHeadersEnabled=true`，并至少提供一个明确的 `Security:KnownProxies` IP 或 `Security:KnownNetworks` CIDR；两项都为空会阻止启动。`KnownProxies`、`KnownNetworks` 支持逗号或分号分隔，`Security:ForwardLimit` 默认 1、只允许 1 到 10。不要信任整个公网或包含普通客户端的宽泛网段，应用端口也应只对可信代理开放。

Swagger 由 `Swagger:Enabled` 控制：未设置时只在 Development 开启，其他环境默认关闭；只有显式设置为 `true` 才会在非 Development 环境注册 `/swagger`。

### 自身 Bootstrap

Workbench API 启动时幂等登记自己的 OAuth 客户端和 API resource（见 `WorkbenchClientCredentialHostedService`）：客户端 `client_secret_basic`、PKCE 必需、grant 为 `authorization_code` + `refresh_token`，scope 为 `openid profile offline_access` 加 `Auth:Audience`；若数据库密钥与 `Auth:ClientSecret` 不一致会按配置同步。`openid`/`profile`/`offline_access` 是静态 scope，不会建成资源。

## 管理接口与权限

管理接口需要 Workbench Cookie 或合法的 Workbench Bearer access token。**并非所有接口都是系统管理员专属**：

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
2. `GET /api/auth/login` 生成 `state`、`nonce`、S256 PKCE，同时生成浏览器关联随机值并写入 HttpOnly Cookie；服务端只保存该值的 SHA-256 绑定，不把关联值放进 `authorizeUrl`。
3. 登录流程默认 10 分钟过期，进程内最多保留 10,000 个；登录发起端点按客户端 IP 固定窗口限制为每分钟 20 次。容量或频率超限返回 429。
4. Discovery 文档按 Authority 跨请求缓存 5 分钟，并合并并发获取；`/api` 与 `/signin-oidc` 响应统一设置 `Cache-Control: no-store` 和 `Pragma: no-cache`。
5. Provider 回调 `GET /signin-oidc` 时，API 先校验 state 格式和浏览器关联 Cookie，再以比较绑定值的方式原子取出并删除流程状态。相同 state 只能消费一次，错误浏览器即使拿到 state 也不能消耗该流程。
6. API 在服务端兑换授权码、校验 ID Token，写入 `.NexusAuth.Workbench` HttpOnly Cookie，跳前端 `/auth/callback`。
7. Cookie 默认 `SecurePolicy=Always`；Development 为本地 HTTP 显式使用 `SameAsRequest`。Cookie 验证在 access token 距过期超过 1 分钟时执行 introspection，接近过期时用 refresh token 刷新并复检，随后续到新的 24 小时窗口；失败则拒绝并清 Cookie。
8. `POST /api/auth/logout` 使用最长 5 秒的 best-effort 窗口撤销 refresh token 和 access token；`SignOutProvider=true` 时从 Discovery 获取 endsession 端点并返回带 `id_token_hint`、`post_logout_redirect_uri` 的 `logoutUrl`。无论撤销或 Discovery 是否失败，最终都会清除本地 Cookie。

管理 API 的结果过滤器保留原始 HTTP 状态和 `204 No Content`；未知异常记录日志后返回固定文案的 HTTP 500，不把异常详情返回客户端。

Workbench API 在接入处通过 `AddNexusAuth(...).UseInMemory()` 显式选择进程内存储。多实例部署必须把同一浏览器的登录发起和回调粘性路由到同一实例，或引用可选的 `NexusAuth.Extension.Redis` 并改用 `.UseRedis(...)`。其他实现通过 `.UseFlowStateStore<T>()` 注册（默认 Scoped）。内存存储与发现缓存通过接口注册为 Singleton，Redis 存储与 OIDC 服务为 Scoped，释放由容器管理。注册示例见 [Workbench 手册](../document/Workbench使用手册.md#63-扩展复用addnexusauth)。所有实例还必须共享 ASP.NET Core Data Protection key ring。

## 网关模式

`NEXUSAUTH_WORKBENCH_GATEWAY_ENABLED=true` 时，API 只接受网关转发的 Bearer access token，`AuthController` 被移除，`/api/auth/config`、`/api/auth/login`、`/signin-oidc`、`/api/auth/logout` 返回 404，`/api/auth/me` 仍在。Bootstrap 仍登记客户端但不开 grant/回调。Dashboard 不会随该开关切换登录方式，网关登录客户端需另行创建。详见 [Workbench 使用手册第 11 节](../document/Workbench使用手册.md#11-网关模式边界)。

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
- [APISIX 网关对接手册](../document/13-APISIX网关对接手册.md)
