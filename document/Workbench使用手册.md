# Workbench 使用手册：Workbench API + Dashboard

本文只讲 NexusAuth Workbench。Workbench 是一套管理工作台，由服务端 `Workbench API` 和浏览器界面 `Dashboard` 两部分组成，二者必须成对部署和使用。

NexusAuth 仓库里还有另一个独立应用：[NexusAuth.Host](NexusAuth.Host使用手册.md)（身份认证服务器）。Host 负责登录、授权、令牌签发和身份协议，可以单独运行。Workbench 通过 OIDC 委托 Host 完成人员登录，再在浏览器里提供用户、客户端、资源、凭证和审计的管理界面。Workbench 不是认证服务，Dashboard 也不是 OAuth 客户端。

本文以当前仓库源码为准重写，不沿用旧的“快速启动”混合说明。文中所有相对路径都以本文件所在目录（`document/`）为基准，或者已在正文中说明以仓库根目录为基准。

## 目录

建议首次部署时按以下顺序阅读：

- [1. 定位与组成](#1-定位与组成)
- [2. 前置条件](#2-前置条件)
- [3. 用根目录 Docker Compose 启动](#3-用根目录-docker-compose-启动)
- [4. 从源码本地运行](#4-从源码本地运行)
- [5. Workbench API 配置](#5-workbench-api-配置)
- [6. 认证与自身 Bootstrap](#6-认证与自身-bootstrap)
- [7. BFF 会话、Cookie、Bearer、刷新与退出](#7-bff-会话cookiebearer刷新与退出)
- [8. Dashboard 菜单、路由与操作](#8-dashboard-菜单路由与操作)
- [9. 管理对象与操作步骤](#9-管理对象与操作步骤)
- [10. 权限边界](#10-权限边界)
- [11. 构建与部署](#11-构建与部署)
- [12. 排错](#12-排错)
- [13. 相关文档](#13-相关文档)

## 1. 定位与组成

Workbench 由以下项目和进程组成：

| 组件 | 项目路径（仓库根目录为基准） | 作用 | 容器名 |
| --- | --- | --- | --- |
| Workbench API | `admin/src/NexusAuth.Workbench.Api` | OIDC BFF（登录、回调、Cookie、刷新、登出）+ JWT Bearer + 管理 API + Swagger | `admin-api` |
| Dashboard | `admin/src/NexusAuth.Workbench.Dashboard` | React 管理界面，只通过 BFF 的 `/api` 同源访问 | `dashboard` |
| OIDC 扩展 | `admin/src/NexusAuth.Extension` | 流程状态、Basic 后台请求适配与原生元数据预热；不管理会话 Cookie、登录端点、PKCE 或协议校验 | 随 API 编译 |

Workbench 还依赖两个外部组件：

| 依赖 | 说明 |
| --- | --- |
| NexusAuth.Host | 身份认证服务器，提供授权码 + PKCE、OIDC Discovery、令牌和 introspection。启动与配置见 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) |
| PostgreSQL | 数据存储。当前实现下 Workbench 与 Host **共用同一个 `nexusauth` 数据库和 `nexusauth` schema** |

角色分工要记住两点：

- Host 面向终端用户和业务系统，负责“这个用户是谁、能不能登录、能拿到什么令牌”。
- Workbench 面向运维和管理员，负责“有哪些用户、客户端、资源、凭证、审计记录，怎么管理它们”。Dashboard 使用 Cookie BFF，客户端密钥只留在 Workbench API 服务端；access token 和 refresh token 位于 API 签发的加密 HttpOnly 票据 Cookie 中，浏览器 JavaScript 读不到明文。无头 API 客户端使用 `Authorization: Bearer`，不需要会话 Cookie。

## 2. 前置条件

### 2.1 基础环境

- .NET 10 SDK：编译和源码方式运行 Workbench API。
- Node.js 22 与 npm：源码方式运行 Dashboard（仓库 Dockerfile 使用 `node:22-alpine`）。
- PostgreSQL 16：可以自建，也可以使用根目录 `docker-compose.yml` 里的 `db` 服务。
- Docker 与 Docker Compose（可选）：只想用容器时使用。

### 2.2 数据库与 schema

Workbench 和 Host 指向同一个库和 schema。数据库必须先初始化：

- 仓库根的 `production-init.sql` 是当前 schema 的唯一来源，创建 `nexusauth` schema 及其中的全部表和索引。
- 容器的 `db` 服务在**全新的数据卷**上，通过 `docker/postgres-init.sh` 执行该脚本；已有数据卷不会再执行。
- `production-init.sql` **不创建数据库**，只创建 `nexusauth` schema 及其中的表、索引。创建数据库由 `docker/postgres-init.sh` 负责：它先建 `nexusauth` 库，再连到该库执行 `production-init.sql`。
- 手工初始化要分两步：先连到默认库创建数据库 `nexusauth`，再**连到 `nexusauth` 库**执行脚本，不要试图在 `postgres` 库上运行：

  ```bash
  psql --username=postgres --dbname=postgres --command='CREATE DATABASE nexusauth'
  psql --username=postgres --dbname=nexusauth --file=production-init.sql
  ```

- 该脚本不写入 demo 用户或初始管理员；初始管理员由 Host 的 BootstrapAdmin 配置创建（见 [Host 使用手册](NexusAuth.Host使用手册.md)）。Workbench 自身的 OAuth 客户端和资源由 Workbench API 启动时登记，不需要手工写表。

如果 schema 未初始化，Workbench API 会在启动或首次访问数据库时报错，而不是自动建表。当前代码没有在启动时执行 EF Core 迁移或 `EnsureCreated`。

### 2.3 必须先有一个可用的 Host

Workbench 的 Cookie BFF 和 Bearer API 调用都以 Host 为身份与令牌签发方。请先按 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) 把 Host 启动起来，至少满足：

- Host 通过 OIDC Discovery（`<Authority>/.well-known/openid-configuration`）暴露授权、令牌、JWKS 和 introspection 端点。
- Host 的 OIDC issuer 与 Workbench 配置的 `Auth:Authority` 完全一致；同一个 Authority 用于浏览器授权跳转、Discovery、令牌、JWKS、introspection 和 ID Token issuer 校验。
- 浏览器和 Workbench API 进程都能直接访问该 Authority。容器内的 `localhost` 指向容器自身；跨网段或跨容器时应使用同一个可达域名或 IP，并通过网络 DNS 或入口路由访问 Provider。

Host 默认关闭 SMTP，因此没有邮件配置也能启动。需要邮件功能时，在 Host appsettings 中设置 `Smtp:Enabled=true` 并提供完整配置；启用后 `ValidateOnStart` 会在必需项缺失时阻止启动：

```bash
dotnet run --project src/NexusAuth.Host
```

Compose 方式下需要配置邮件时，可按 [SMTP 配置与发信验证](SMTP发信试验.md) 提供 Host appsettings 文件。

需要创建初始管理员时，在 Host 的 appsettings 中配置 `BootstrapAdmin`；未配置时跳过，详见 [管理员初始化](NexusAuth.Host使用手册.md#4-管理员初始化)。

## 3. 用根目录 Docker Compose 启动

所有 Compose 命令都在**仓库根目录**执行。

### 3.1 一套命令启动全部

本地预览必须同时叠加基础编排和本地覆盖文件。基础 `docker-compose.yml` 保留 HTTPS 安全默认，`docker-compose.local.yml` 只对隔离的本地预览显式启用 HTTP：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

需要邮件功能时，按 [SMTP 配置与发信验证](SMTP发信试验.md) 在 Host appsettings 中启用并提供完整配置。

会启动四个服务：

| 服务 | 容器内端口 | 宿主映射 | 说明 |
| --- | --- | --- | --- |
| `db` | 5432 | `127.0.0.1:33210` | PostgreSQL 16 |
| `sso` | 8080 | `127.0.0.1:5100` | NexusAuth.Host，登录与令牌签发 |
| `admin-api` | 8080 | **不映射** | Workbench API，只在 Compose 网络内，由 Dashboard 的 Nginx 反代 |
| `dashboard` | 80 | `127.0.0.1:5560` | Nginx 托管的 Dashboard，并代理 `/api`、`/signin-oidc` |

浏览器访问 `http://localhost:5560`。Dashboard 的 Nginx（`admin/src/NexusAuth.Workbench.Dashboard/nginx.conf`）把 `/api` 和 `/signin-oidc` 转发到 `http://admin-api:8080`，其余路径回退到 `index.html`。因此对外的 OIDC 回调是 `http://localhost:5560/signin-oidc`，真正处理它的是 Workbench API，`admin-api` 本身不需要对外暴露端口。

根 `docker-compose.yml` 只向 `admin-api` 注入 `ASPNETCORE_ENVIRONMENT` 和 `TZ`，业务配置全部来自 JSON 文件：

- 默认把 `docker/workbench.appsettings.json` 只读挂到 `/app/appsettings.${ASPNETCORE_ENVIRONMENT:-Production}.json`。这是生产安全样例，`Auth:Authority` 和 `Auth:ClientSecret` 留空，数据库密码为占位值；复制为私有文件并填写后才能启动；
- `NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE` 只选择要挂载的 JSON 路径，是 Compose 插值变量，不注入应用容器；需要自定义部署文件时只替换这一项；
- `docker-compose.local.yml` 把该变量切换为 `./docker/workbench.local.appsettings.json`。本地文件单独提供 HTTP Authority `http://localhost:5100`、回调 `http://localhost:5560/signin-oidc`、登出回调 `http://localhost:5560/`、开发密钥和开发数据库连接，只用于隔离的本地预览；
- 不把真实密钥写入仓库模板、`deploy/direct/workbench.env.example` 或 Compose `environment`。生产密钥应生成后放入私有配置文件，并与 Host 中该客户端的密钥一致（见 [6.2](#62-自身-bootstrap-登记了什么)）。

`deploy/direct/workbench.env.example` 只列出 ASP.NET Core 宿主环境与监听地址等启动参数，不承载 Workbench 业务配置。框架原生环境变量和命令行参数仍然可用，但当前部署流程以环境 JSON 为准。

### 3.2 拆分启动

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build -d db sso admin-api dashboard
docker compose -f docker-compose.yml -f docker-compose.local.yml logs -f admin-api
docker compose -f docker-compose.yml -f docker-compose.local.yml down
```

`admin-api` 依赖 `db` 健康且 `sso` 健康后才启动；`dashboard` 依赖 `admin-api` 健康。已有数据卷时，`db` 的健康检查仍会通过，但初始化脚本不会再跑。需要重建数据库时删除卷 `nexusauth-db-data`（会丢数据，谨慎操作）。

## 4. 从源码本地运行

源码方式要开**多个终端**：数据库、Host、Workbench API 各占一个，Dashboard 再占一个。不要把它们串成一条命令。

### 4.1 数据库

最简单的做法是借用 Compose 的 `db` 服务（宿主端口 `33210`）：

```bash
# 终端 1：确保数据库已初始化
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d db
```

或用本地 PostgreSQL 16，自行执行 `production-init.sql` 初始化。两种方式都要保证 Workbench 连接串里的库名、schema、账号密码一致。

### 4.2 Host

```bash
# 终端 2：先启动身份认证服务器
dotnet run --project src/NexusAuth.Host
```

如果要用真实邮件，在 Host appsettings 中启用并配置 SMTP。Host 详细配置见 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md)。

### 4.3 Workbench API

源码方式下 Workbench API 的认证与 Bootstrap 配置**必填**，缺任何一项都会在启动时抛 `InvalidOperationException` 并退出。基础 `appsettings.json` 提供完整结构和安全默认值，`appsettings.Development.json` 已准备本地数据库、Authority 与回调地址，但 `Auth:ClientSecret` 留空，首次运行前需要在本机文件中填写。

该开发配置文件包含共享数据库连接串：

```json
{
  "ConnectionStrings": {
    "Default": "Host=127.0.0.1;Port=33210;Database=nexusauth;Username=nexusauth;Password=nexusauth_dev_password;Search Path=nexusauth"
  }
}
```

也就是说，源码开发时数据库端口默认就是 Compose 的 `33210`，连的就是那套共享 `nexusauth` 库。

基础 `appsettings.json` 的 `Auth:Authority` 和 `Auth:ClientSecret` 为空，`Auth:RequireHttpsMetadata=true`。`appsettings.Development.json` 为本机开发显式提供：

- `Auth:Authority=http://localhost:5100` 和 `Auth:RequireHttpsMetadata=false`；
- `Auth:RedirectUri=http://localhost:5273/signin-oidc`；
- `Auth:PostLogoutRedirectUri=http://localhost:5273/`；
- 本地开发数据库连接与 `Security:CookieSecurePolicy=SameAsRequest`。

在 `appsettings.Development.json` 中为 `Auth:ClientSecret` 填入长随机开发密钥后（启动时 Bootstrap 会把它同步到同库的 Host 客户端登记），启动 Workbench API（终端 3）：

```bash
dotnet run --project admin/src/NexusAuth.Workbench.Api
```

API 默认监听 `http://localhost:5051`（见 `Properties/launchSettings.json`），Swagger 在 `http://localhost:5051/swagger`。启动日志里会看到 Bootstrap 创建或更新客户端的信息。需要修改连接串或其他业务项时，直接编辑当前环境的 appsettings 文件。

### 4.4 Dashboard

```bash
# 终端 4
npm --prefix admin/src/NexusAuth.Workbench.Dashboard install
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run dev
```

打开 `http://localhost:5273`（Vite 默认端口）。`vite.config.ts` 默认把 `/api` 和 `/signin-oidc` 代理到本机 API `http://localhost:5051`，与 API 的启动配置一致。API 部署在其他地址时，再同步调整这两个代理目标。

本地源码方式的最小回调约定：

- Provider 登记的 `redirect_uri` = `http://localhost:5273/signin-oidc`（浏览器先到 Vite，再由 Vite 代理转发到 API）；
- `post_logout_redirect_uri` = `http://localhost:5273/`；
- Dashboard 的 Vite 代理目标 = API 地址（`http://localhost:5051`）；
- API 的 `Auth:Authority` = Host 地址（`http://localhost:5100`）。

四处地址必须自洽，改一处就要同步其余。回调地址要写浏览器实际访问的 Vite 地址（`5273`），API 自身监听 `http://localhost:5051` 不是回调地址。

## 5. Workbench API 配置

### 5.1 appsettings 配置表

`admin/src/NexusAuth.Workbench.Api/appsettings.json` 提供完整的安全基础结构；`appsettings.Development.json` 只覆盖本机开发项。所有功能通过 `IOptions<T>` 读取配置。

| 配置键 | 安全基础或本地开发值 | 说明 |
| --- | --- | --- |
| `ConnectionStrings:Default` | 基础配置为空；Development 为 `127.0.0.1:33210` 开发库 | PostgreSQL 连接串；生产使用私有值 |
| `Auth:Authority` | 基础为空；Development 为 `http://localhost:5100` | Provider issuer 与 OIDC Discovery 基础地址 |
| `Auth:Audience` | `nexusauth.workbench.api` | API resource 与 Bearer access token 的 audience，同时补入登录 scope；OIDC ID token 的 audience 使用 `Auth:ClientId` |
| `Auth:ClientId` | `nexusauth.workbench` | Workbench OAuth 客户端 ID |
| `Auth:ClientSecret` | 空 | 首次运行或部署前必须填写；不提交真实值 |
| `Auth:RedirectUri` | 基础为 `http://localhost:5051/signin-oidc`；Development 为 `http://localhost:5273/signin-oidc` | 浏览器实际访问的 OIDC 回调 |
| `Auth:PostLogoutRedirectUri` | 基础为 `http://localhost:5273/`；Development 同值 | Provider 登出后返回地址 |
| `Auth:Scope` | `openid profile offline_access nexusauth.workbench.api` | 申请的 scope |
| `Auth:RequireHttpsMetadata` | 基础 `true`；Development `false` | 非本地环境保持 `true` |
| `Auth:SignOutProvider` | `true` | 是否生成 Provider 全局登出地址 |
| `Auth:AllowedBearerClientIds` | `[]` | 额外允许调用 Workbench API 的 OAuth 客户端 ID；`Auth:ClientId` 始终默认允许 |
| `Auth:FlowState:Provider` | `InMemory` | 多实例改为 `Redis` |
| `Auth:FlowState:Lifetime` / `Capacity` | `00:10:00` / `10000` | 待完成登录流程有效期和容量 |
| `Auth:FlowState:Redis:Configuration` | 空 | Redis 模式必填 |
| `Auth:FlowState:Redis:KeyPrefix` / `Database` | `NexusAuth:oidc:flow` / `-1` | Redis 键前缀与数据库编号；使用默认数据库时设为 `-1` |
| `Security:CookieSecurePolicy` | 基础 `Always`；Development `SameAsRequest` | 会话 Cookie Secure 策略 |
| `Security:ForwardedHeadersEnabled` | `false` | 默认不信任转发头 |
| `Security:ForwardLimit` | `1` | 可信代理链最大跳数，允许 1 到 10 |
| `Security:KnownProxies` / `KnownNetworks` | 空 | 启用转发头时至少填写一项 |
| `Swagger:Enabled` | `null` | 未配置时仅 Development 开启；显式布尔值优先 |
| `Bootstrap:ResourceName` | `nexusauth.workbench.api` | Bootstrap API resource 名称 |
| `Bootstrap:ResourceDisplayName` | `NexusAuth Workbench API` | 资源显示名 |
| `Bootstrap:ResourceDescription` | `Workbench administration API` | 资源说明 |
| `Bootstrap:ClientName` | `NexusAuth Workbench` | Bootstrap 客户端显示名 |
| `Bootstrap:ClientDescription` | `Workbench BFF client` | 客户端说明 |
| `Smtp:Enabled` | `false` | Workbench 邮件功能默认关闭 |
| `Smtp:Host` / `Port` / `Security` | 空 / `465` / `SslOnConnect` | 启用邮件后填写并选择 `SslOnConnect` 或 `StartTls` |
| `Smtp:Username` / `Password` / `FromAddress` | 空 | 启用邮件后必填；密钥只放私有配置 |
| `Smtp:FromName` | `NexusAuth Workbench` | 发件人显示名 |
| `Smtp:VerificationCodeCooldownSeconds` / `VerificationCodeLifetimeMinutes` | `60` / `5` | 验证码冷却和有效期 |

Development 文件中的 HTTP Authority、`RequireHttpsMetadata=false` 和 `SameAsRequest` 只用于隔离本地开发。非本地环境应保持 `RequireHttpsMetadata=true`、`CookieSecurePolicy=Always`，并使用 HTTPS Authority 和回调地址。

### 5.2 配置来源与部署文件

业务配置统一使用 appsettings，不再由 `WorkbenchConfigurationExtensions` 映射 `NEXUSAUTH_WORKBENCH_*` 单下划线别名。框架原生环境变量和命令行参数仍由 ASP.NET Core 支持，但不是仓库当前的主要部署流程。

| 场景 | 配置文件 | 说明 |
| --- | --- | --- |
| 所有环境 | `admin/src/NexusAuth.Workbench.Api/appsettings.json` | 完整安全基础结构，无真实密钥 |
| 源码开发 | `admin/src/NexusAuth.Workbench.Api/appsettings.Development.json` | 本地数据库、HTTP localhost、开发回调和 `ClientSecret` 空位 |
| Compose 默认 | `docker/workbench.appsettings.json` | 挂载为 `/app/appsettings.${ASPNETCORE_ENVIRONMENT:-Production}.json`；Authority、密钥为空，DB 密码占位 |
| Compose 本地 | `docker/workbench.local.appsettings.json` | 由 `docker-compose.local.yml` 显式选择，提供 HTTP localhost、开发密钥和开发数据库 |
| 独立部署样例 | `deploy/direct/workbench.appsettings.example.json` | `Auth:Authority=https://sso.example.com`、密钥为空；复制为同目录 `workbench.appsettings.json` |
| 独立部署运行 | `deploy/direct/workbench.appsettings.json` | 已 gitignore；`deploy/direct/compose.yml` 只读挂到 `/app/appsettings.Production.json` |
| 启动参数 | `deploy/direct/workbench.env.example` | 只示意 `ASPNETCORE_ENVIRONMENT`、`ASPNETCORE_URLS` 等宿主设置 |

生产 Compose 建议从安全模板复制私有文件，再设置唯一路径变量：

```bash
cp docker/workbench.appsettings.json docker/workbench.private.appsettings.json
# 编辑私有文件，填写 Authority、ClientSecret、数据库密码和公开域名
export NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE=./docker/workbench.private.appsettings.json
docker compose -f docker-compose.yml up -d admin-api
```

`docker/workbench.private.appsettings.json` 已被 gitignore。Compose 的文件路径覆盖只有：

```bash
NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE=/path/to/workbench.appsettings.json \
docker compose -f docker-compose.yml up -d admin-api
```

该变量只决定挂载哪一个文件，不会把其中每个配置项拆成容器环境变量。JSON 内的连接串不会展开 `${POSTGRES_PASSWORD}` 等变量；修改 PostgreSQL 用户名或密码时，也要同步修改私有 JSON 的 `ConnectionStrings:Default`。生产部署可直接使用等价挂载：

```bash
docker run --rm \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -v "$PWD/deploy/direct/workbench.appsettings.json:/app/appsettings.Production.json:ro" \
  <workbench-api-image>
```

`deploy/direct/workbench.appsettings.example.json` 使用 `https://sso.example.com` 等占位值。复制为同目录的 `workbench.appsettings.json` 后填写 `Auth:ClientSecret`、数据库密码和实际域名；该运行文件已被 gitignore。源码或发布目录需要 Production 覆盖时，可使用同样已被 gitignore 的 `admin/src/NexusAuth.Workbench.Api/appsettings.Production.json`。密钥可通过密码管理器或随机生成器产生，生成后只写入私有 JSON 和 Host 对应客户端。

基础 `appsettings.json` 已提供 `Bootstrap:ResourceName`、`ResourceDisplayName`、`ResourceDescription`、`ClientName` 和 `ClientDescription` 默认值，环境文件只需覆盖实际需要变化的值；`launchSettings.json` 只负责本地 Development 环境和监听地址，不注入 Bootstrap 元数据。

启动时会集中校验：`Authority` 必须是非空绝对 HTTP(S) 基础 URI，且不含用户信息、查询或片段；`RequireHttpsMetadata=true` 时必须是 HTTPS。`RedirectUri`、`PostLogoutRedirectUri`、`Scope` 必须有值。任何一项不满足都会抛出异常并阻止启动。

`Auth:ClientId` 和 `Auth:ClientSecret` 的配置首尾空白在启动校验、Bootstrap 和协议调用中统一去除；内部空格与其他字符保持原样。Workbench 固定使用 `client_secret_basic`，token 请求由专用 `HttpClient` delegating handler 写入 Basic 认证头。

### 5.3 单一 Authority

这是 Workbench 最容易配错的一项：

| 键 | 面向谁 | 作用 |
| --- | --- | --- |
| `Auth:Authority` | 浏览器 + Workbench 进程 | Provider 的唯一基础地址。用于标准 OIDC handler 的 Discovery、JWKS、授权码、nonce 和 ID Token 校验；Workbench 内部客户端也从同一发现文档读取 refresh、introspection 和 revoke 端点 |

要点：

- `Auth:Authority` 必须与 Host 配置和 Discovery 返回的 issuer 完全一致。任何 scheme、host、port 或路径差异都会导致登录或令牌协议调用失败。
- 浏览器和 Workbench API 进程必须都能直接访问这一地址。容器里的 `localhost` 指容器自身；跨容器部署应给浏览器和各工作负载使用同一个可解析域名或 IP，并配置网络 DNS 或入口路由到 Provider。
- 不要用 `sso:8080` 等容器服务名替代 `Auth:Authority`，也不要再引入内网地址、HTTP 地址改写或其他仅服务端可达的地址映射。
- `Auth:Authority` 必须是绝对 HTTP(S) 基础 URI，不能带用户信息、查询串或片段。

### 5.4 可信代理、Swagger 与缓存边界

Workbench 默认不处理 `X-Forwarded-For`、`X-Forwarded-Proto` 等转发头。需要由反向代理提供客户端 IP 和原始协议时：

- 设置 `Security:ForwardedHeadersEnabled=true`；
- 至少配置一个 `Security:KnownProxies` IP 或 `Security:KnownNetworks` CIDR；
- 根据需要设置 `Security:ForwardLimit`，默认 1，允许 1 到 10；
- 只信任实际代理节点或专属代理网段，并让 Workbench API 端口只对可信代理开放。

`KnownProxies` 和 `KnownNetworks` 使用逗号或分号分隔，不是 JSON 数组。启用转发头但没有明确代理或网络会阻止启动。入口代理应覆盖客户端自行提交的转发头；多级代理需要按实际链路配置可信节点和跳数。

Swagger 未显式配置时仅在 `Development` 环境注册；其他环境默认不暴露 `/swagger`。确需启用时显式设置 `Swagger:Enabled=true`，并继续使用 Workbench 的默认授权边界保护。

所有 `/api` 和 `/signin-oidc` 响应都会写入 `Cache-Control: no-store` 与 `Pragma: no-cache`。反向代理也应为这两个路径关闭响应缓存，避免登录状态、授权码交换和用户数据被缓存。

## 6. 认证与自身 Bootstrap

### 6.1 认证流程

1. Dashboard 启动时请求 `GET /api/auth/me`（`AllowAnonymous`）。已登录返回 `isAuthenticated=true` 和最小用户信息；未登录返回 `401` 与 `isAuthenticated=false`，前端判定未登录后显示登录页。
2. 用户点击登录，前端请求 `GET /api/auth/login` 并取得 JSON `authorizeUrl`。微软 `AddOpenIdConnect` handler 负责 state、nonce、PKCE、回调处理以及 ID Token 的签名、issuer、audience、时效和 nonce 校验。
3. Workbench 流程状态默认 10 分钟过期、最多 10,000 个；登录发起端点按客户端 IP 限流。容量或频率超限返回 `429`。使用反向代理时应按下文配置可信转发头，否则多个用户会共享代理 IP 的登录额度。
4. 启动后台服务预热 Discovery。预热失败只记录日志并按有限延迟重试，不阻塞应用启动；运行时元数据与 JWKS 由标准配置管理器刷新。
5. Host 回调 `GET /signin-oidc`。OIDC handler 完成标准校验后，API 写入 `.NexusAuth.Workbench` HttpOnly Cookie，并跳转前端 `/auth/callback`。
6. Dashboard 之后的管理请求使用 Cookie。未认证返回 `401`，已认证但没有管理访问权限返回 `403`。
7. Cookie 验证在 access token 接近过期时使用内部 `IWorkbenchTokenClient` 刷新，并复检 token 状态；失败则拒绝并清除 Cookie。
8. 无头 API 客户端改用 `Authorization: Bearer <access_token>`。请求只要出现任意 `Authorization` 头，默认策略就选择 Bearer；头为空、方案错误或 token 无效时不会回退到同请求中的 Cookie。
9. Bearer 使用 `Auth:Authority` 作为 issuer、`Auth:Audience` 作为 audience，只接受 RS256 签名且 `token_use=access_token` 的 access token。`client_id` 等于 `Auth:ClientId` 或出现在 `Auth:AllowedBearerClientIds` 后，再由 `IUserTokenValidator` 检查禁用、撤销、token epoch 和 jti 黑名单。
10. `POST /api/auth/logout` 对 Cookie 调用请求撤销 refresh token 和 access token，按配置发起 Provider 登出，返回 JSON `logoutUrl` 并清除本地 Cookie；Bearer 调用只请求 Provider revoke 本次保存的 access token，不清 Cookie、不发起 Provider 登出，`logoutUrl` 为空。revoke 失败只记录日志且接口仍返回 200，不保证远端撤销已完成。

Workbench 同时支持 Cookie BFF 和 JWT Bearer，默认认证方案是 `Workbench` 策略方案。Dashboard 始终使用 Cookie BFF；无头 API 客户端只发送 Bearer，不依赖 Cookie。微软 `AddOpenIdConnect` 负责 Discovery、JWKS、授权码兑换、PKCE、nonce 和 ID Token 校验。Workbench 通过专用 `HttpClient` delegating handler 将 `client_secret_basic` 写入 token 请求头，不调用 `HandleCodeRedemption`，也不重复执行 `ProtocolValidator`。

JWT Bearer 与 OIDC 登录复用标准 `OpenIdConnectOptions.ConfigurationManager`，Discovery 和 JWKS 只有一套框架缓存。OIDC ID token 的 audience 是 `Auth:ClientId`，只用于登录；Bearer access token 的 audience 是 `Auth:Audience`，不能与 ID token 混用。

无头调用示例：

```bash
curl -H "Authorization: Bearer $NEXUSAUTH_ACCESS_TOKEN" \
  http://localhost:5051/api/auth/me
```

内部 `IWorkbenchTokenClient` 只承担 refresh、introspection 和 revoke，并复用同一份标准 OIDC 元数据。它不负责浏览器登录、PKCE 生成、ID Token 校验或 Bearer 验签。

默认 `UseInMemory()` 只适用于单实例或带粘性路由的部署。多实例应使用 Redis 流程存储，并共享 ASP.NET Core Data Protection key ring。

认证端点与鉴权：

| 端点 | 鉴权 | 说明 |
| --- | --- | --- |
| `GET /api/auth/config` | 匿名 | 返回可公开的 `authority`、`clientId`、`redirectUri` |
| `GET /api/auth/login` | 匿名 | 返回 `authorizeUrl` |
| `GET /signin-oidc` | 匿名 | OIDC 回调处理 |
| `GET /api/auth/me` | 匿名入口，内部执行 Workbench 默认访问策略 | 返回 `isAuthenticated`、最小用户信息和兼容字段 `isAdministrator` |
| `POST /api/auth/logout` | 需登录（`WorkbenchAuthenticatedUser`） | Cookie 调用清 Cookie、可能返回 `logoutUrl`；Bearer 调用只请求 Provider revoke 本次 access token，不清 Cookie、不发起 Provider 登出 |

### 6.2 自身 Bootstrap 登记了什么

Workbench API 启动时由 [`WorkbenchClientCredentialHostedService`](../admin/src/NexusAuth.Workbench.Api/Bootstrap/WorkbenchClientCredentialHostedService.cs) 幂等地登记自己的 OAuth 客户端和资源，**不需要手工创建**。它读取 `Auth` 和 `Bootstrap` 配置：

- 依赖项（缺任意一项即启动失败）：`Auth:ClientId`、`Auth:ClientSecret`、`Auth:RedirectUri`、`Auth:PostLogoutRedirectUri`、`Auth:Audience`、`Bootstrap:ResourceName`、`Bootstrap:ResourceDisplayName`、`Bootstrap:ClientName`。
- 注册或更新 API resource：名称取 `Bootstrap:ResourceName`，`audience` 取 `Auth:Audience`，显示名与描述取 `Bootstrap:ResourceDisplayName` / `ResourceDescription`。
- 注册或更新 OAuth 客户端：`client_id` 取 `Auth:ClientId`，名称取 `Bootstrap:ClientName`，描述取 `ClientDescription`。
- 客户端能力：grant 始终为 `authorization_code` + `refresh_token`，`require_pkce=true`，token 端点认证固定为 `client_secret_basic`，scope 为静态的 `openid profile offline_access` 加上 `Auth:Audience`，回调取 `Auth:RedirectUri`、登出回调取 `Auth:PostLogoutRedirectUri`。
- 关联关系：把该客户端与该 resource 关联，使签发的 access token 带正确 `aud`。
- 密钥同步：如果数据库里的客户端密钥与 `Auth:ClientSecret` 不一致，会用配置值替换共享密钥哈希并标注来源，因此改密钥只需改配置再重启，不用手工改库。

注意：`openid`、`profile`、`offline_access` 是客户端静态 scope，不会被当作 API resource。Bootstrap 只登记 Workbench 自己的资源，其他应用和资源请在 Dashboard 里登记。

### 6.3 OIDC 后通道与流程状态扩展

Workbench 的浏览器登录完全由标准 `AddOpenIdConnect` 和 Cookie 中间件处理。注册入口见 [`WorkbenchAuthenticationExtensions`](../admin/src/NexusAuth.Workbench.Api/Authentication/WorkbenchAuthenticationExtensions.cs)。内部 [`IWorkbenchTokenClient`](../admin/src/NexusAuth.Workbench.Api/Authentication/Tokens/IWorkbenchTokenClient.cs) 只提供 refresh、introspection 和 revoke，并复用标准 `OpenIdConnectOptions.ConfigurationManager` 中缓存的发现文档；JWT Bearer 也使用同一个 metadata manager。Basic 客户端认证由 Extension 的 `UseOpenIdConnectClientSecretBasic(scheme)` 为专用 `HttpClient` 适配；它不接管授权码兑换，也不重复验证 nonce 或 ID Token。

`admin/src/NexusAuth.Extension` 已不再是 OAuth/OIDC 协议 SDK。它提供可复用的 OIDC 流程状态、Basic 后台请求适配及原生元数据启动预热，并与标准 `AddOpenIdConnect` 组合使用，不依赖 `NexusAuth.Shared` 或 `NexusAuth.Workbench`，不提供登录端点，也不生成 PKCE。接入说明见 [Extension README](../admin/src/NexusAuth.Extension/README.md)，各注册项用途和复用边界见 [Workbench 认证注册说明](Workbench认证注册说明.md)。

```csharp
services.AddOpenIdConnectFlowState().UseInMemory();
// 或
services.AddOpenIdConnectFlowState().UseRedis(options =>
    configuration.GetSection("Auth:FlowState:Redis").Bind(options));
```

`AddOpenIdConnectFlowState()` 会自动以 Scoped 注册轻量 `OpenIdConnectFlowStateEvents`。内置事件在 `MessageReceived` 自动校验流程，成功后调用 `base` 委托，失败时不执行后续用户委托；授权跳转和 Provider 登出跳转会检查端点，但不写应用的 callback、post-logout callback、会话或错误页。应用通过 `EventsType` 启用：

```csharp
services.AddAuthentication()
    .AddOpenIdConnect("oidc", options =>
    {
        options.EventsType = typeof(OpenIdConnectFlowStateEvents);
    });
```

Workbench 使用继承类型 `WorkbenchOpenIdConnectEvents`。它在调用基类检查后补充完整 `RedirectUri`、`PostLogoutRedirectUri`、24 小时会话、RS256 限制和前端错误跳转。自定义事件继承 `OpenIdConnectFlowStateEvents` 时，覆盖 `MessageReceived`、`RedirectToIdentityProvider` 或 `RedirectToIdentityProviderForSignOut` 应调用对应 `base` 方法，以保留流程和端点检查。

```json
{
  "Auth": {
    "FlowState": {
      "Provider": "InMemory",
      "Lifetime": "00:10:00",
      "Capacity": 10000,
      "Redis": {
        "Configuration": "127.0.0.1:6379",
        "KeyPrefix": "NexusAuth:oidc:flow",
        "Database": 0
      }
    }
  }
}
```

`OpenIdConnectFlowStateOptions` 提供 `Provider`、`Lifetime` 和 `Capacity`；`OpenIdConnectFlowState` 是扩展入口。`UseFlowStateStore<T>()` 可替换自定义存储。`Auth:FlowState:Provider` 默认 `InMemory`，多实例部署应改为 `Redis`。Redis 实现继续保证浏览器绑定、原子一次消费和跨实例共享容量；所有实例使用相同的 Lifetime、Capacity、Redis 数据库和 KeyPrefix。

Workbench 使用 `AddOpenIdConnectFlowState().UseConfiguration(configuration.GetSection("Auth:FlowState"))` 一次完成接入；扩展通过 Options 绑定和启动校验选择存储，Workbench 不再维护 Provider switch 或重复的 Redis/TTL 校验。TTL、容量和浏览器绑定属于流程状态扩展；24 小时会话、刷新/内省、撤销检查和 JSON 登录/登出契约仍属于 Workbench Cookie 事件，不进入 SDK。

多实例部署必须同时共享并持久化 ASP.NET Core Data Protection key ring；Redis 只共享流程状态，不能替代 Cookie 和 nonce/correlation 票据所需的 key ring。

### 6.4 API 结果与错误边界

使用 `WorkbenchApiResult` 的管理控制器会保留 action 返回的 HTTP 状态，成功结果不会统一改成 200；`204 No Content` 保持无响应体。参数错误、未找到、认证或权限失败、业务冲突分别保留对应的 4xx 状态。

未识别异常统一返回 HTTP `500` 和固定安全文案，详细异常只写入服务端日志，不向客户端暴露堆栈、数据库错误或内部消息。已知的唯一键和并发冲突映射为 `409`，不会被包装成成功。

## 7. BFF 会话、Cookie、Bearer、刷新与退出

### 7.1 Cookie

- 名称：`.NexusAuth.Workbench`；
- `HttpOnly=true`、`SameSite=Lax`；基础 `SecurePolicy=Always`，Development 配置显式覆盖为 `SameAsRequest` 以便本地 HTTP；
- Workbench API 的控制器是 `[ApiController]` 且没有自定义 401 重定向事件，未认证访问受保护接口时返回 HTTP `401`（无权限返回 `403`），不会自动 302 跳转；登录由前端在检测到 401 后显式请求 `GET /api/auth/login` 发起；
- 本地会话有效期固定为 24 小时，开启滑动续期；
- Cookie 的加密票据中包含 `access_token`、`refresh_token`、`id_token` 和 `expires_at`。票据由 ASP.NET Core Data Protection 加密并以 `HttpOnly` Cookie 发送给浏览器，因此浏览器 JavaScript 不能读取或解密，也拿不到明文令牌。

Workbench 的 24 小时会话与 Provider 登录页“几天内免登录”是两层配置，互不相同。Provider 那层由 `NEXUSAUTH_LOGIN_FLOW_REMEMBER_ME_LIFETIME_DAYS` 控制。

多副本部署必须共享并持久化 ASP.NET Core Data Protection key ring。否则一个实例签发的 Cookie 在另一个实例上无法解密，用户会随机被登出。仓库 Compose 已把 key ring 放到命名卷 `nexusauth-admin-keys`。

### 7.2 Bearer

- 默认认证方案 `Workbench` 检查请求是否存在 `Authorization` 头；没有时转发到 Cookie，存在任意值时只转发到 Bearer。
- Bearer token 必须是 Host 签发、RS256 签名、未过期且 audience 为 `Auth:Audience` 的 access token；ID token 或缺少 `token_use=access_token` 的 token 会被拒绝。
- `client_id` 默认允许 `Auth:ClientId`，其他客户端必须逐项加入 `Auth:AllowedBearerClientIds`。
- 框架验签后，`WorkbenchJwtBearerEvents` 调用 `IUserTokenValidator` 检查用户禁用、会话撤销、token epoch 和 jti 黑名单。
- Bearer 与 OIDC 登录共用标准 OIDC 的 metadata manager。Bearer 客户端不需要 Cookie，也不应把浏览器 Cookie 作为回退身份。

无头 API 调用：

```bash
curl -H "Authorization: Bearer $NEXUSAUTH_ACCESS_TOKEN" \
  http://localhost:5051/api/auth/me
```

Dashboard 不读取或转发 access token，仍只通过同源 Cookie 调用 `/api`。

### 7.3 刷新与状态检查

每次 Cookie 验证（[`WorkbenchCookieAuthenticationEvents.ValidatePrincipal`](../admin/src/NexusAuth.Workbench.Api/Authentication/Events/WorkbenchCookieAuthenticationEvents.cs)）都会检查保存的令牌：

- access token 距过期**超过 1 分钟**：调用 introspection，确认 token 仍 active、主体与当前用户一致、客户端是 Workbench 客户端。不满足则拒绝 Principal 并清 Cookie。
- access token 距过期**不超过 1 分钟**：用 refresh token 刷新，再对新 token introspection 复检，然后保存轮换后的 access/refresh token，并把 Cookie 会话续到新的 24 小时窗口。
- 刷新采用单飞（single-flight）合并：并发请求共享同一次刷新结果，避免同一 refresh token 被并发消费。
- token 缺失、格式错误、用户不匹配、introspection 失败或 refresh 失败：拒绝并清 Cookie，前端下次受保护请求收到 401 后回登录页。

### 7.4 退出

`POST /api/auth/logout` 的行为：

- Cookie 调用读取 Cookie 里的 `id_token`，在最长 5 秒的共享 best-effort 窗口内，依次尝试撤销 refresh token 和 access token；单个令牌撤销失败只记录日志，不阻止后续清理。
- Cookie 调用在 `Auth:SignOutProvider=true` 且存在 `id_token` 时，从缓存的 Discovery 文档读取 `end_session_endpoint`，返回带 `id_token_hint` 和 `post_logout_redirect_uri` 的 `logoutUrl`；Discovery 或端点缺失不会阻止本地退出。
- 无论撤销或 Discovery 是否失败，Cookie 调用的 `finally` 分支始终清除本地 Workbench Cookie。
- Bearer 调用只请求 Provider revoke 本次保存的 access token，不清 Cookie、不发起 Provider 登出，并返回空 `logoutUrl`。revoke 失败只记录日志且接口仍返回 200，不保证远端撤销已完成。同一浏览器已有的 Cookie 会话保持可用。
- 两类调用通常都返回 HTTP `200` 和 `{ "logoutUrl": "..." }`；Dashboard 仅在 Cookie 退出拿到非空地址后继续跳转，完成 Provider 全局登出。

Dashboard 的 Cookie 退出只清本地 Cookie 不等于结束 Host 的全局登录。要彻底退出需要在 `logoutUrl` 上确认 Host 的登出；如果该地址为空，本地会话已经清除，但 Provider 全局会话仍需通过其他方式结束。Bearer 退出只处理当前 access token，不代表浏览器 Cookie 会话或 Provider 全局会话结束。

## 8. Dashboard 菜单、路由与操作

Dashboard 的菜单和路由都由 `src/api/auth.ts` 里的菜单定义动态生成（`use-user-detail..tsx` 依据它注册路由），不依赖后端菜单接口。当前可见菜单如下。

| 菜单 | 路由 | 页面组件 |
| --- | --- | --- |
| 仪表盘 | `/dashboard` | `pages/dashboard` |
| 用户管理 | `/user-management` | `pages/user-management` |
| 应用中心 → 应用管理 | `/oauth/client-management` | `pages/client-management` |
| 应用中心 → SCIM 凭证 | `/oauth/scim-credential-management` | `pages/scim-credential-management` |
| 应用中心 → 开放 API 凭据 | `/oauth/open-api-credential-management` | `pages/open-api-credential-management` |
| 应用中心 → 登录记录 | `/oauth/login-audit-management` | `pages/login-audit-management` |
| 应用中心 → 服务资源 | `/oauth/api-resource-management` | `pages/api-resource-management` |
| 智 V V | `/ai-agent` | `pages/ai-agent` |
| 验证配置 → 字段取值配置 | `/field-value-mapping-config` | `pages/field-value-mapping-config` |
| 验证配置 → 产品配置 | `/product-config` | `pages/product-config` |
| 验证配置 → saleRefId 映射配置 | `/sale-ref-id-mapping-config` | `pages/sale-ref-id-mapping-config` |
| 验证配置 → 公司主体映射配置 | `/company-subject-mapping-config` | 菜单项存在，但 `pages/` 下没有对应组件，打开会落到错误页 |
| 验证配置 → 字典配置 | `/dictionary-config` | `pages/dictionary-config` |

还有两个不显示在菜单但在路由表里的地址：新增应用 `/oauth/client-management/create`、编辑应用 `/oauth/client-management/edit/:id`。兼容地址 `/oauth/user-management` 指向同一用户页。

“智 V V”和“验证配置”下的页面属于当前仓库的扩展或占位模块，不是 NexusAuth 通用 OAuth 管理能力，遇到报错时先确认对应后端是否提供。菜单基于单页应用路由，刷新由 Nginx/`index.html` 回退承接。

## 9. 管理对象与操作步骤

以下操作都在 Dashboard 中完成，底层对应 Workbench API 的控制器。接口以 Swagger（`/swagger`）为准。

### 9.1 用户管理

入口：左侧“用户管理”（`/user-management`）。页面顶部是统计和筛选（关键词支持用户名、昵称、邮箱、手机号；状态可筛启用/禁用），列表按用户名、联系信息、用户类型、状态、更新时间、创建时间展示。

操作：

- **查看**：打开详情抽屉，显示用户名、昵称、邮箱、手机号、姓名、职务、用户类型、状态、时间、SCIM External ID、语言/地区、时区，以及该用户的 Passkey 凭据。
- **新增用户**：填用户名、密码、昵称等，用户类型等可选；创建时用户名、邮箱、手机号不得重复；密码以 BCrypt 哈希存储。
- **编辑**：修改昵称、联系方式、姓名、职务、用户类型、语言/地区、时区。
- **启停**：`PATCH /api/users/{id}/status`，禁用会同时作废 token 和会话。
- **重置密码**：`POST /api/users/{id}/reset-password`，同时作废该用户 token 和会话。
- **吊销 token**：`POST /api/users/{id}/revoke-tokens`，吊销该用户在所有客户端的访问令牌、刷新令牌和 SSO 会话，但不禁用账号；用户可重新登录，重新启用账号不会恢复旧令牌。
- **删除**：`DELETE /api/users/{id}`，同时作废 token 和会话。

所有 `/api/users` 操作统一受第 10 节的 Workbench 默认访问策略保护；默认授权 handler 要求操作者是数据库中启用的系统账号（`IsActive=true` 且 `IsSystemAccount=true`）。系统账号对象不能被这些接口修改、禁用、重置密码、删除或吊销，该对象保护由用户管理服务执行，控制器不再重复判定操作者权限。详见 [10. 权限边界](#10-权限边界)。

### 9.2 Passkey 凭据查看与撤销

入口：用户详情抽屉里的“Passkey 凭据”分区。它读取 `GET /api/users/{id}/passkeys`，展示凭据的显示名、创建时间、最近使用、撤销时间、备份资格/是否已备份，并可撤销单个凭据。

撤销调用 `POST /api/users/{id}/passkeys/{credentialId}/revoke`，需要填写原因；撤销后该凭据不能再用于登录，并会退出该用户所有设备、撤销现有令牌。`UserPasskeysController` 与其他管理控制器一样，由 Workbench 默认访问策略统一保护。

### 9.3 应用（OAuth 客户端）管理

入口：“应用中心 → 应用管理”（`/oauth/client-management`）。列表可按名称、Client ID 或服务资源 Key 搜索，按状态和认证方式筛选。

- **新增**：`/oauth/client-management/create`。填 Client ID、应用名称、描述，选择允许的 scope、允许的 grant type、关联的服务资源，填登录回调地址和登出回调地址，选认证方式（`client_secret_basic`、`client_secret_post`、`client_secret_jwt`、`private_key_jwt`），设置是否启用 PKCE 和应用状态。选 `private_key_jwt` 时提供 JWKS/JWKS URI，或让后端自动生成 RSA 密钥对（自动生成的私钥明文只在创建后显示一次）；`client_secret_jwt` 属于对称密钥方式，不涉及自动生成 RSA/JWKS。
- **编辑**：`/oauth/client-management/edit/:id`。可改名称、描述、scope、grant、服务资源、回调、认证方式、JWKS 和状态；Client ID 创建后不可改。
- **生成/重置密钥**：对应 `POST /api/clients/{id}/credentials` 与 `.../credentials/reset`。**新密钥只在操作成功时展示一次**，重置会让旧密钥立即失效。自动生成 JWKS 时，私钥明文也只在创建后显示一次。
- **删除**：`DELETE /api/clients/{id}`。

当前 Host 的 `private_key_jwt` 校验只读取已登记的 JWKS，不会下载 JWKS URI；虽然表单提供 URI 输入，仅配置 URI 的客户端仍无法完成认证。请使用 JWKS 或自动生成方式，详见 [Host 使用手册](NexusAuth.Host使用手册.md#13-已知限制)。

### 9.4 服务资源（API Resource）

入口：“应用中心 → 服务资源”（`/oauth/api-resource-management`）。

- **新增资源**：填服务唯一标识（Name）、显示名称、Audience、状态、描述。
- **编辑/启停**：`PUT /api/api-resources/{id}`、`PATCH /api/api-resources/{id}/status`。
- **删除**：`DELETE /api/api-resources/{id}`。
- 在应用管理里把资源关联给客户端后，签发 token 的 `aud` 才会使用该资源的 Audience。Workbench 自身的资源由 Bootstrap 自动维护，一般不需要手工改。

### 9.5 登录记录（审计）

入口：“应用中心 → 登录记录”（`/oauth/login-audit-management`）。可按关键词、结果（成功/失败）、来源应用筛选，列表显示登录账号、结果、来源应用、IP、失败原因、客户端（User-Agent）、登录时间。数据来自 `GET /api/login-audits`。

### 9.6 SCIM 凭证

入口：“应用中心 → SCIM 凭证”（`/oauth/scim-credential-management`）。SCIM 面向用户同步：应用用这些凭证调用 **Host** 的 `/scim/v2`。

Host 可通过 appsettings 中的 `Scim:Enabled=false` 关闭 SCIM 接口；关闭后接口返回 404，Workbench 凭证管理仍然可用。配置优先级和接口清单见 [Host SCIM 接口说明](NexusAuth.Host使用手册.md#7-scim-20-用户供给)。

- 创建时可填名称、scope（`scim:read`、`scim:write`）和过期时间；凭证明文只在创建时返回一次，服务端只存哈希。
- 更新可改名称、scope、过期时间和启用状态；撤销调用 `POST /api/scim-credentials/{id}/revoke`。
- 对应的 Host 端（按 HTTP 方法分档）：`GET /scim/v2/ServiceProviderConfig`、`GET /scim/v2/ResourceTypes`、`GET /scim/v2/Schemas`、`GET /scim/v2/Schemas/{id}`、`GET /scim/v2/Users`、`GET /scim/v2/Users/{id}` 需要 `scim:read`；`POST /scim/v2/Users`、`PUT /scim/v2/Users/{id}`、`PATCH /scim/v2/Users/{id}`、`DELETE /scim/v2/Users/{id}` 需要 `scim:write`。只带 `scim:read` 的凭证调用写方法会返回 403。

运行仓库的 SCIM 容器回归时，先登录受保护的 Workbench，不要复用正式集成凭据。分别创建名为 `scim-container-read`（仅 `scim:read`）和 `scim-container-read-write`（`scim:read` + `scim:write`）的短有效期测试凭据，把创建时各显示一次的 token 仅传入测试进程：

```bash
NEXUSAUTH_TEST_SCIM_BASE_URL=http://localhost:5100/scim/v2/ \
NEXUSAUTH_TEST_SCIM_READ_TOKEN='<read-only-token>' \
NEXUSAUTH_TEST_SCIM_READ_WRITE_TOKEN='<read-write-token>' \
dotnet test tests/NexusAuth.Host.IntegrationTests/NexusAuth.Host.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~ScimContainerApiTests'
```

缺少任一变量时测试会以 xUnit Skip 结束。默认 Compose、`postgres-init.sh` 和已有数据库卷都不会恢复或播种固定测试 token；测试结束后在 Workbench 撤销专用凭据。

### 9.7 开放 API 凭证

入口：“应用中心 → 开放 API 凭据”（`/oauth/open-api-credential-management`）。开放 API 凭证用于机器读取 **Host** 的只读目录接口。

- 创建时选用途（`application` 或 `service_resource`）和过期时间，对应 scope 分别为 `application:read`、`service_resource:read`，由后端自动绑定；用途创建后不可修改。凭证明文只在创建时返回一次，服务端只存哈希。
- 更新可改名称、过期时间和启用状态；撤销调用 `POST /api/open-api-credentials/{id}/revoke`。
- 对应的 Host 端：`application` 凭证可调用 `GET /openapi/v1/applications`，`service_resource` 凭证可调用 `GET /openapi/v1/service-resources`。

### 9.8 仪表盘

入口：默认页 `/dashboard`。它是登录后的落地页；顶部标签页默认固定保留“仪表盘”，其余打开的页面以标签形式堆叠，支持关闭。

仪表盘通过现有 Workbench BFF API 读取真实数据：登录记录、成功数和失败数来自 `/api/login-audits`，用户数来自 `/api/users`，应用数来自 `/api/clients`，服务资源数来自 `/api/api-resources`；登录成功率由成功数与登录记录总数计算，近期列表展示审计接口返回的真实登录字段。单个请求失败时保留上次成功取得的数据并显示部分加载提示，首次失败则显示“暂不可用”或空状态。当前 API 没有按日期聚合的认证趋势和按应用聚合的调用排行，因此这两个区域明确显示暂不支持，不使用示例数据。

## 10. 权限边界

Workbench API 站点采用**默认拒绝**的授权边界。这些注册仅在 `WorkbenchApiModule` 中生效，不影响独立的 `NexusAuth.Host` 站点及其开放 API。Workbench 使用框架默认的 `IAuthorizationPolicyProvider` 和 `IAuthorizationService`，由 `AddWorkbenchAuthorization()` 将同一个管理访问策略同时设为默认策略和 fallback 策略：

- 没有 `[Authorize]` 或 `[AllowAnonymous]` 的端点使用 `FallbackPolicy`；
- 只有裸 `[Authorize]` 的端点使用 `DefaultPolicy`。

该策略要求用户已通过认证，并包含一个 `WorkbenchAccessRequirement`。默认方案 `Workbench` 在无 `Authorization` 头时使用 Cookie，出现任意 `Authorization` 头时只使用 Bearer。因此，新增到 Workbench API 的控制器和 action 会默认受保护；公开入口通过显式标注 `[AllowAnonymous]` 放行；命名策略使用各自声明的规则，公开入口应逐项审查。API 未认证请求返回 `401`；已认证但未通过访问评估的请求返回 `403`。Swagger 也受该策略保护，访问者可通过 Workbench Cookie 或 Bearer 认证并具备管理访问权限。不存在的路径同样先经过 fallback，只有通过授权后才返回 `404`。

访问权限规则直接位于 `WorkbenchAccessAuthorizationHandler` 中。它继承框架原生的 `AuthorizationHandler<WorkbenchAccessRequirement>`，并通过 scoped 的 `IAuthorizationHandler` 注册参与 ASP.NET Core 授权；其直接依赖 `IUserRepository`，按 `sub`（或 `NameIdentifier`）查询数据库，只允许 `IsActive=true` 且 `IsSystemAccount=true` 的系统账号。handler 和端点授权不能依赖 `/api/auth/me` 的返回值或该端点的鉴权元数据来判断 UI 或 API 访问；`/me` 只是当前身份与兼容字段的查询入口。

下列管理入口及其全部读写操作由默认访问策略保护：

- `/api/clients`、`/api/api-resources`、`/api/client-metadata`；
- `/api/login-audits`、`/api/scim-credentials`、`/api/open-api-credentials`；
- `/api/users`、`/api/users/{id}/passkeys`。

管理控制器不再分别硬编码旧的 Administrator 策略，也不再在 action 内重复执行管理员判定。系统账号对象本身不可被这些用户管理接口修改、禁用、重置密码、删除或吊销的保护仍由用户管理服务执行。

授权相关类型分别位于 `admin/src/NexusAuth.Workbench.Api/Authorization/` 下的同名文件中，命名空间为 `NexusAuth.Workbench.Api.Authorization`。以下示例需引入该命名空间。

`AddWorkbenchAuthorization()` 通过 `services.AddAuthorization` 注册默认策略、fallback 策略和仅要求认证的命名策略，并以 scoped 生命周期注册访问 handler：

```csharp
services.AddAuthorization(options =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new WorkbenchAccessRequirement())
        .Build();
    options.DefaultPolicy = policy;
    options.FallbackPolicy = policy;
    options.AddPolicy(
        WorkbenchAuthorizationPolicies.AuthenticatedUser,
        p => p.RequireAuthenticatedUser());
});
services.AddScoped<IAuthorizationHandler, WorkbenchAccessAuthorizationHandler>();
```

扩展访问判定时，应用应继续使用官方的 `IAuthorizationHandler` 注册点。只需移除 `ImplementationType` 为 `WorkbenchAccessAuthorizationHandler` 的对应 descriptor，再注册继承了 `AuthorizationHandler<WorkbenchAccessRequirement>` 的新 handler；内置 handler 保持不变：

```csharp
builder.Services.AddWorkbenchAuthorization();

var descriptor = builder.Services.FirstOrDefault(descriptor =>
    descriptor.ServiceType == typeof(IAuthorizationHandler) &&
    descriptor.ImplementationType == typeof(WorkbenchAccessAuthorizationHandler));
if (descriptor is not null)
{
    builder.Services.Remove(descriptor);
}

builder.Services.AddScoped<IAuthorizationHandler,
    CustomWorkbenchAccessAuthorizationHandler>();
```

`/api/auth/config`、`/api/auth/login`、`/signin-oidc` 和 `/api/auth/me` 明确保留 `[AllowAnonymous]`。`POST /api/auth/logout` 显式使用命名策略 `WorkbenchAuthenticatedUser`（`WorkbenchAuthorizationPolicies.AuthenticatedUser`），只要求已登录，因此普通用户也能退出。

`/api/auth/me` 虽然允许匿名访问，但已登录请求会通过框架的 `IAuthorizationService`，对 `IOptions<AuthorizationOptions>.Value.DefaultPolicy` 执行同一访问规则。兼容字段 `isAdministrator` 表示当前 Workbench 管理访问规则是否通过，不再表示某个单独的管理员策略或控制器内判断；Dashboard 可用它展示提示，但该字段不能代替服务端端点授权。默认 fallback、匿名入口和仅要求认证的命名策略保持不变，Workbench 的这些策略也不会影响独立的 Host 站点。

Workbench 管理请求支持两种身份入口：Dashboard 使用 `.NexusAuth.Workbench` Cookie；无头客户端使用 Bearer access token。只要请求存在任意 `Authorization` 头，认证就只走 Bearer，不会回退到 Cookie。Bearer 必须满足 `Auth:Audience` audience、`token_use=access_token`、客户端白名单和 `IUserTokenValidator` 的撤销检查；OIDC ID token 的 audience 仍是 `Auth:ClientId`。

## 11. 构建与部署

### 11.1 构建

从仓库根目录执行：

```bash
dotnet build admin/NexusAuth.Admin.sln
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run build
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run lint
```

Dashboard 的 `npm run build` 先跑 `tsc -b` 再跑 Vite build，没有单独的 `npm run typecheck` 脚本，类型检查含在 build 里。Workbench API 目标框架是 `net10.0`。

### 11.2 镜像与端口

- Workbench API 镜像：`admin/src/NexusAuth.Workbench.Api/Dockerfile`，SDK 10 构建、`aspnet:10.0` 运行，监听 8080。
- Dashboard 镜像：`admin/src/NexusAuth.Workbench.Dashboard/Dockerfile`，Node 22 构建、Nginx 1.27 托管，监听 80。
- 根 Compose 映射：`dashboard` → 宿主 `5560`；`db` → 宿主 `33210`；`sso` → 宿主 `5100`；`admin-api` 不映射宿主端口。

### 11.3 生产要点

- Dashboard 对外只暴露一个域名，由反向代理把 `/api` 和 `/signin-oidc` 转发到 Workbench API，保留 `Host`、`X-Forwarded-Proto` 和 Cookie；如需客户端 IP，同时转发 `X-Forwarded-For`。
- 只有确需处理转发头时才启用 `Security:ForwardedHeadersEnabled`，并配置明确的 `Security:KnownProxies` 或 `Security:KnownNetworks`。应用端口只对可信代理开放，入口覆盖客户端伪造的转发头。
- 生产配置使用 HTTPS Authority、`Auth:RequireHttpsMetadata=true` 和 `Security:CookieSecurePolicy=Always`。本地 Compose 通过 `docker-compose.local.yml` 选择 `docker/workbench.local.appsettings.json` 并显式使用 HTTP，该文件不进入生产。
- Provider 登记的 `redirect_uri`、`post_logout_redirect_uri` 与 API 配置逐字符一致，且使用 HTTPS。
- `Auth:Authority` 使用浏览器和 Workbench API 进程都能直接访问的 HTTPS 地址，并与 Provider issuer 和 token 的 `iss` 完全一致；通过 DNS 或入口路由保证该地址在两端可达。
- `Auth:Audience` 与 Workbench API resource 和 Bearer access token 的 audience 一致；OIDC ID token 使用 `Auth:ClientId`。
- 无头调用方使用的客户端 ID 默认必须等于 `Auth:ClientId`，其他可信客户端要显式加入 `Auth:AllowedBearerClientIds`。不要让任意客户端仅凭能签发 access token 就获得 Workbench 管理权限。
- 多副本共享 Data Protection key ring；默认内存 flow 必须使用粘性路由，或将 `Auth:FlowState:Provider` 设为 `Redis` 并使用共享的 Redis 配置。数据库使用持久卷并备份。
- `/api` 与 `/signin-oidc` 在应用侧强制 `no-store`，反向代理也应为这两个路径禁用响应缓存。
- 不提交或在镜像里固化真实 `ClientSecret`；生产只挂载私有 `appsettings.Production.json`，不使用 `docker/workbench.local.appsettings.json` 或仓库内置开发值，也不执行 `demo/seed.sql`。

## 12. 排错

| 现象 | 可能原因 | 处理 |
| --- | --- | --- |
| API 启动即退出，日志出现 `Auth:... is required` | 当前环境 appsettings 缺少认证或 Bootstrap 值 | 按第 5.1 节补齐当前环境文件 |
| API 启动即退出，提示转发头必须配置可信代理/网络 | 开启 `Security:ForwardedHeadersEnabled` 但两项均为空 | 配置 `KnownProxies` 或 `KnownNetworks`，不要信任宽泛公网 |
| API 启动失败，提示数据库相关错误 | schema 未初始化或连接串不对 | 执行 `production-init.sql`；核对连接串与端口 |
| 浏览器登录后立刻跳回登录页 | 回调地址、Issuer 或 Cookie 域不一致 | 核对第 4.4 节的四处地址，确认 `Authority` 与 Host issuer 一致 |
| 本地 HTTP 登录后 Cookie 未保存 | 误用了基础 `Always` 而不是 Development/本地文件的 `SameAsRequest` | 源码使用 `appsettings.Development.json`；Compose 同时加载 `docker-compose.local.yml` 与本地 JSON |
| 本地 Dashboard 请求失败，命中内网地址 | Vite 代理仍是 `192.168.31.200:9100` | 把 `vite.config.ts` 的 `/api`、`/signin-oidc` 目标改成实际 API |
| API 报无法访问 Provider Discovery | 容器内把 `localhost` 当成 Host，或网络 DNS/入口路由不可达 | 使用浏览器和 Workbench API 都能直达的同一 Authority；容器内不要用 `localhost` 或容器服务名替代 |
| 多实例下回调偶尔失败 | 登录流程保存在默认进程内存储，回调落到了其他实例 | 使用粘性路由，或将 `Auth:FlowState:Provider` 设为 `Redis` |
| 管理接口返回 403 | 当前访问授权 handler 未通过 | 默认授权 handler 下用启用状态的系统账号登录；替换授权 handler 时核对其判定（见第 10 节） |
| Bearer 请求返回 401，但同一浏览器 Cookie 仍有效 | 请求存在 `Authorization` 头时只验证 Bearer，不回退 Cookie；也可能是 audience、`token_use`、client ID、用户状态或 token 撤销状态不符合要求 | 去掉 `Authorization` 才走 Cookie；Bearer 调用核对 `Auth:Audience`、`token_use=access_token`、`Auth:AllowedBearerClientIds` 和 `IUserTokenValidator` |
| 多实例下用户随机被登出 | Data Protection key ring 未共享 | 共享并持久化 key ring |
| 无法查看已创建的 SCIM/开放 API 凭证明文 | 明文只在创建时返回一次 | 重新创建凭证并立即保存 |

Host 侧的问题（登录页、邮件、Passkey、令牌签发、SCIM 服务端行为）请看 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) 和 [SMTP 配置与发信验证](SMTP发信试验.md)。

## 13. 相关文档

- [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md)：独立身份认证服务器的启动、配置与协议接入。
- [Workbench API 统一登录接入模板](Workbench-API统一登录接入模板.md)：服务端接入样例。
- [Workbench Dashboard 统一登录接入模板](Workbench-Dashboard统一登录接入模板.md)：前端登录样例。
- [OAuth/OIDC 协议设计](11-OAuth-OIDC协议设计.md)：端点、参数校验和令牌规则。
- [SMTP 配置与发信验证](SMTP发信试验.md)：Host 邮件配置与发信排错。
- [Workbench API README](../admin/src/NexusAuth.Workbench.Api/README.md)：API 目录与配置入口。
- [Dashboard README](../admin/src/NexusAuth.Workbench.Dashboard/README.md)：前端入口。
