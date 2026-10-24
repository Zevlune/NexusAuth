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
- [7. BFF 会话、Cookie、刷新与退出](#7-bff-会话cookie刷新与退出)
- [8. Dashboard 菜单、路由与操作](#8-dashboard-菜单路由与操作)
- [9. 管理对象与操作步骤](#9-管理对象与操作步骤)
- [10. 权限边界](#10-权限边界)
- [11. 网关模式边界](#11-网关模式边界)
- [12. 构建与部署](#12-构建与部署)
- [13. 排错](#13-排错)
- [14. 相关文档](#14-相关文档)

## 1. 定位与组成

Workbench 由以下项目和进程组成：

| 组件 | 项目路径（仓库根目录为基准） | 作用 | 容器名 |
| --- | --- | --- | --- |
| Workbench API | `admin/src/NexusAuth.Workbench.Api` | OIDC BFF（登录、回调、Cookie、刷新、登出）+ 管理 API + Swagger | `admin-api` |
| Dashboard | `admin/src/NexusAuth.Workbench.Dashboard` | React 管理界面，只通过 BFF 的 `/api` 同源访问 | `dashboard` |
| 接入扩展 | `admin/src/NexusAuth.Extension` | Workbench 的 OIDC、PKCE、刷新、introspection 客户端实现（`AddNexusAuth`） | 随 API 编译 |

Workbench 还依赖两个外部组件：

| 依赖 | 说明 |
| --- | --- |
| NexusAuth.Host | 身份认证服务器，提供授权码 + PKCE、OIDC Discovery、令牌和 introspection。启动与配置见 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) |
| PostgreSQL | 数据存储。当前实现下 Workbench 与 Host **共用同一个 `nexusauth` 数据库和 `nexusauth` schema** |

角色分工要记住两点：

- Host 面向终端用户和业务系统，负责“这个用户是谁、能不能登录、能拿到什么令牌”。
- Workbench 面向运维和管理员，负责“有哪些用户、客户端、资源、凭证、审计记录，怎么管理它们”。客户端密钥只留在 Workbench API 服务端；access token 和 refresh token 位于 API 签发的加密 HttpOnly 票据 Cookie 中，浏览器 JavaScript 读不到明文。

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

Workbench 的默认模式（BFF 模式）需要 Host 提供 OIDC 登录。请先按 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) 把 Host 启动起来，至少满足：

- Host 通过 OIDC Discovery（`<Authority>/.well-known/openid-configuration`）暴露授权、令牌、JWKS 和 introspection 端点。
- Host 的 `Jwt:Issuer` 与 Workbench 配置的 `Auth:Authority` 一致，因为 Workbench 同时用它做浏览器跳转和 issuer 校验。
- Workbench API 所在进程能访问 Host 的令牌和内省端点；容器或跨网段时用 `Auth:BackchannelAuthority` 指向内部地址。

Host 源码方式默认关闭 SMTP，因此没有邮件配置也能启动。只有需要邮件功能时，才显式设置 `NEXUSAUTH_SMTP_ENABLED=true` 并提供完整配置；启用后 `ValidateOnStart` 仍会在任何必需项缺失时立即阻止启动：

```bash
# 方式一：提供完整可用的 SMTP 配置，并配置初始管理员
NEXUSAUTH_SMTP_ENABLED=true \
NEXUSAUTH_SMTP_PASSWORD=你的授权码 \
NEXUSAUTH_SMTP_HOST=smtp.example.com \
NEXUSAUTH_SMTP_PORT=465 \
NEXUSAUTH_SMTP_SECURITY=SslOnConnect \
NEXUSAUTH_SMTP_USERNAME=sender@example.com \
NEXUSAUTH_SMTP_FROM_ADDRESS=sender@example.com \
NEXUSAUTH_BOOTSTRAP_ADMIN_USERNAME=admin \
NEXUSAUTH_BOOTSTRAP_ADMIN_PASSWORD=请替换为强密码 \
dotnet run --project src/NexusAuth.Host

# 方式二：本地不验证邮件时保持默认关闭（初始管理员仍必填）
NEXUSAUTH_SMTP_ENABLED=false \
NEXUSAUTH_BOOTSTRAP_ADMIN_USERNAME=admin \
NEXUSAUTH_BOOTSTRAP_ADMIN_PASSWORD=请替换为强密码 \
dotnet run --project src/NexusAuth.Host
```

Compose 方式下 `docker-compose.yml` 显式传入 `NEXUSAUTH_SMTP_ENABLED=${NEXUSAUTH_SMTP_ENABLED:-false}`，所以默认不启用邮件。需要邮件时，可按 [SMTP 配置与发信验证](SMTP发信试验.md) 第 3 节设置 `NEXUSAUTH_SMTP_ENABLED=true`，并用 Compose override 提供完整 SMTP 配置。

## 3. 用根目录 Docker Compose 启动

所有 Compose 命令都在**仓库根目录**执行。

### 3.1 一套命令启动全部

Host 镜像内 `appsettings.json` 和 Compose 默认关闭 SMTP，无邮件配置也可以启动整套服务：

```bash
docker compose up --build
```

需要邮件功能时，设置 `NEXUSAUTH_SMTP_ENABLED=true` 并按 [SMTP 配置与发信验证](SMTP发信试验.md) 第 3 节提供完整配置。

会启动四个服务：

| 服务 | 容器内端口 | 宿主映射 | 说明 |
| --- | --- | --- | --- |
| `db` | 5432 | `127.0.0.1:33210` | PostgreSQL 16 |
| `sso` | 8080 | `127.0.0.1:5100` | NexusAuth.Host，登录与令牌签发 |
| `admin-api` | 8080 | **不映射** | Workbench API，只在 Compose 网络内，由 Dashboard 的 Nginx 反代 |
| `dashboard` | 80 | `127.0.0.1:5560` | Nginx 托管的 Dashboard，并代理 `/api`、`/signin-oidc` |

浏览器访问 `http://localhost:5560`。Dashboard 的 Nginx（`admin/src/NexusAuth.Workbench.Dashboard/nginx.conf`）把 `/api` 和 `/signin-oidc` 转发到 `http://admin-api:8080`，其余路径回退到 `index.html`。因此对外的 OIDC 回调是 `http://localhost:5560/signin-oidc`，真正处理它的是 Workbench API，`admin-api` 本身不需要对外暴露端口。

Compose 为 Workbench API 注入的关键值（见根 `docker-compose.yml` 的 `admin-api` 服务）：

- `Auth:Authority` 取 `${NEXUSAUTH_JWT_ISSUER:-http://localhost:5100}`，与 Host 的 issuer 对齐；
- `Auth:BackchannelAuthority` 默认 `http://sso:8080`，让容器内的服务端请求走 Compose 网络；
- `Auth:RedirectUri` 默认 `http://localhost:5560/signin-oidc`；`Auth:PostLogoutRedirectUri` 默认 `http://localhost:5560/`，可通过 `NEXUSAUTH_WORKBENCH_AUTH_POST_LOGOUT_REDIRECT_URI` 覆盖；
- `Auth:ClientSecret` 由 `${WORKBENCH_CLIENT_SECRET:-c57cf0e110c54ad2ac5591b99801b852}` 注入；
- 连接串与 Bootstrap 名称也由环境变量提供。

容器默认的 `WORKBENCH_CLIENT_SECRET` 是仓库内置的开发值，只适合本地。生产必须用密钥管理注入，并保证与 Host 数据库里的客户端密钥一致（见 [6.2](#62-自身-bootstrap-登记了什么)）。

### 3.2 拆分启动

```bash
docker compose up --build -d db sso admin-api dashboard
docker compose logs -f admin-api
docker compose down
```

`admin-api` 依赖 `db` 健康且 `sso` 健康后才启动；`dashboard` 依赖 `admin-api` 健康。已有数据卷时，`db` 的健康检查仍会通过，但初始化脚本不会再跑。需要重建数据库时删除卷 `nexusauth-db-data`（会丢数据，谨慎操作）。

## 4. 从源码本地运行

源码方式要开**多个终端**：数据库、Host、Workbench API 各占一个，Dashboard 再占一个。不要把它们串成一条命令。

### 4.1 数据库

最简单的做法是借用 Compose 的 `db` 服务（宿主端口 `33210`）：

```bash
# 终端 1：确保数据库已初始化
docker compose up -d db
```

或用本地 PostgreSQL 16，自行执行 `production-init.sql` 初始化。两种方式都要保证 Workbench 连接串里的库名、schema、账号密码一致。

### 4.2 Host

```bash
# 终端 2：先启动身份认证服务器
NEXUSAUTH_SMTP_ENABLED=false \
NEXUSAUTH_BOOTSTRAP_ADMIN_USERNAME=admin \
NEXUSAUTH_BOOTSTRAP_ADMIN_PASSWORD=请替换为强密码 \
dotnet run --project src/NexusAuth.Host
```

如果要用真实邮件，把 `NEXUSAUTH_SMTP_ENABLED=false` 换成完整的 SMTP 环境变量。Host 详细配置见 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md)。

### 4.3 Workbench API

源码方式下 Workbench API 的认证与 Bootstrap 配置**必填**，缺任何一项都会在启动时抛 `InvalidOperationException` 并退出。`admin/src/NexusAuth.Workbench.Api/appsettings.Development.json` 已经准备了共享数据库，但认证密钥和 Bootstrap 名称仍要提供。

该开发配置文件包含共享数据库连接串：

```json
{
  "ConnectionStrings": {
    "Default": "Host=127.0.0.1;Port=33210;Database=nexusauth;Username=nexusauth;Password=nexusauth_dev_password;Search Path=nexusauth"
  }
}
```

也就是说，源码开发时数据库端口默认就是 Compose 的 `33210`，连的就是那套共享 `nexusauth` 库。

`admin/src/NexusAuth.Workbench.Api/appsettings.json` 里的 `Auth:Authority` 当前默认指向局域网地址 `http://192.168.31.200:8100`。本地运行时**必须**把它改到实际 Host 地址，例如 `http://localhost:5100`，并相应调整回调地址。启动命令示例（终端 3）：

```bash
NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY=http://localhost:5100 \
NEXUSAUTH_WORKBENCH_AUTH_CLIENT_ID=nexusauth.workbench \
NEXUSAUTH_WORKBENCH_AUTH_CLIENT_SECRET=请替换为长随机密钥 \
NEXUSAUTH_WORKBENCH_AUTH_REDIRECT_URI=http://localhost:5273/signin-oidc \
NEXUSAUTH_WORKBENCH_AUTH_POST_LOGOUT_REDIRECT_URI=http://localhost:5273/ \
NEXUSAUTH_WORKBENCH_AUTH_SCOPE="openid profile offline_access nexusauth.workbench.api" \
NEXUSAUTH_WORKBENCH_AUTH_AUDIENCE=nexusauth.workbench.api \
NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_NAME=nexusauth.workbench.api \
NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DISPLAY_NAME="NexusAuth Workbench API" \
NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_NAME="NexusAuth Workbench" \
dotnet run --project admin/src/NexusAuth.Workbench.Api
```

API 默认监听 `http://localhost:5051`（见 `Properties/launchSettings.json`），Swagger 在 `http://localhost:5051/swagger`。启动日志里会看到 Bootstrap 创建或更新客户端的信息。如果连接串需要覆盖，用 `NEXUSAUTH_WORKBENCH_CONNECTION_STRINGS_DEFAULT`。

### 4.4 Dashboard

```bash
# 终端 4
npm --prefix admin/src/NexusAuth.Workbench.Dashboard install
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run dev
```

打开 `http://localhost:5273`（Vite 默认端口）。`vite.config.ts` 当前把 `/api` 和 `/signin-oidc` 代理到 `http://192.168.31.200:9100`。本地跑 Workbench API 时，必须把这个代理目标改成 API 的真实地址，例如 `http://localhost:5051`，否则浏览器会打到内网地址并失败。

本地源码方式的最小回调约定：

- Provider 登记的 `redirect_uri` = `http://localhost:5273/signin-oidc`（浏览器先到 Vite，再由 Vite 代理转发到 API）；
- `post_logout_redirect_uri` = `http://localhost:5273/`；
- Dashboard 的 Vite 代理目标 = API 地址（`http://localhost:5051`）；
- API 的 `Auth:Authority` = Host 地址（`http://localhost:5100`）。

四处地址必须自洽，改一处就要同步其余。回调地址要写浏览器实际访问的 Vite 地址（`5273`），API 自身监听 `http://localhost:5051` 不是回调地址。

## 5. Workbench API 配置

### 5.1 appsettings.json 默认值

`admin/src/NexusAuth.Workbench.Api/appsettings.json` 的 `Auth` 段当前默认值如下（注意 Authority 是局域网地址）：

| 键 | 当前默认值 | 说明 |
| --- | --- | --- |
| `Auth:Authority` | `http://192.168.31.200:8100` | Provider 公开基础地址，本地运行需改 |
| `Auth:Audience` | `nexusauth.workbench.api` | access token 的 audience |
| `Auth:RequireHttpsMetadata` | `false` | 是否强制 Discovery 用 HTTPS，生产应设 `true` |
| `Auth:ClientId` | `nexusauth.workbench` | Workbench 客户端 ID |
| `Auth:ClientSecret` | 空 | 机密客户端密钥，必须通过环境变量或 Secret 注入 |
| `Auth:AllowedBearerClientIds` | 空数组 | 额外允许的 Bearer 客户端 ID；始终允许 `Auth:ClientId` |
| `Auth:RedirectUri` | `http://localhost:5051/signin-oidc` | 登录回调 |
| `Auth:PostLogoutRedirectUri` | `http://localhost:5273/` | 登出后回到的前端地址 |
| `Auth:Scope` | `openid profile offline_access nexusauth.workbench.api` | 申请的 scope |
| `Auth:SignOutProvider` | `true` | 登出时是否继续跳 Provider 全局登出 |
| `Gateway:Enabled` | `false` | 是否为网关模式 |

### 5.2 关键环境变量前缀

Workbench API 支持标准 .NET 的双下划线环境变量；此外，为了便于阅读，`admin/src/NexusAuth.Workbench.Api/WorkbenchConfigurationExtensions.cs` 还显式映射了一组 `NEXUSAUTH_WORKBENCH_` 前缀的**单下划线**变量。两种写法都可以，同一键同时提供时以启动时的配置优先级为准。下表列出单下划线便利映射：

| 环境变量 | 映射到配置 | 必填 |
| --- | --- | --- |
| `NEXUSAUTH_WORKBENCH_CONNECTION_STRINGS_DEFAULT` | `ConnectionStrings:Default` | 否（开发环境有默认值） |
| `NEXUSAUTH_WORKBENCH_AUTH_AUTHORITY` | `Auth:Authority` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_BACKCHANNEL_AUTHORITY` | `Auth:BackchannelAuthority` | 否 |
| `NEXUSAUTH_WORKBENCH_AUTH_AUDIENCE` | `Auth:Audience` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_CLIENT_ID` | `Auth:ClientId` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_CLIENT_SECRET` | `Auth:ClientSecret` | 是 |
| `NEXUSAUTH_WORKBENCH_AUTH_REDIRECT_URI` | `Auth:RedirectUri` | 非网关模式必填 |
| `NEXUSAUTH_WORKBENCH_AUTH_POST_LOGOUT_REDIRECT_URI` | `Auth:PostLogoutRedirectUri` | 非网关模式必填 |
| `NEXUSAUTH_WORKBENCH_AUTH_SCOPE` | `Auth:Scope` | 非网关模式必填 |
| `NEXUSAUTH_WORKBENCH_AUTH_REQUIRE_HTTPS_METADATA` | `Auth:RequireHttpsMetadata` | 否 |
| `NEXUSAUTH_WORKBENCH_AUTH_SIGN_OUT_PROVIDER` | `Auth:SignOutProvider` | 否 |
| `NEXUSAUTH_WORKBENCH_GATEWAY_ENABLED` | `Gateway:Enabled` | 否 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_NAME` | `Bootstrap:ResourceName` | 是 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DISPLAY_NAME` | `Bootstrap:ResourceDisplayName` | 是 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_RESOURCE_DESCRIPTION` | `Bootstrap:ResourceDescription` | 否 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_NAME` | `Bootstrap:ClientName` | 是 |
| `NEXUSAUTH_WORKBENCH_BOOTSTRAP_CLIENT_DESCRIPTION` | `Bootstrap:ClientDescription` | 否 |

`NEXUSAUTH_SMTP_*` 系列（`NEXUSAUTH_SMTP_ENABLED`、`HOST`、`PORT`、`SECURITY`、`USERNAME`、`PASSWORD`、`FROM_ADDRESS`、`FROM_NAME`、`VERIFICATION_CODE_COOLDOWN_SECONDS`、`VERIFICATION_CODE_LIFETIME_MINUTES`）由 Workbench API 一并读取并映射到 `Smtp:*`，与 Host 共享同样的校验规则。

`NEXUSAUTH_LUCK_LOGGING_*` 覆盖 `LuckLogging:*` 日志配置。未列入单下划线映射的键可以用标准 .NET 双下划线形式（例如 `Auth__BackchannelAuthority`），或者由编排直接注入配置键。

启动时 `RequireAuthConfiguration` 会集中校验：`Authority` 必须是非空绝对 HTTP(S) URI；非网关模式下 `RedirectUri`、`PostLogoutRedirectUri`、`Scope` 必须有值；`BackchannelAuthority` 必须是合法的绝对 HTTP(S) 基础地址（不含用户信息、查询、片段）。任何一项不满足都会抛出异常并阻止启动。

`Auth:ClientId` 和 `Auth:ClientSecret` 的配置首尾空白在启动校验、Bootstrap 和协议调用中统一去除；内部空格与其他字符保持原样。发送 Basic 认证时，ID 与密钥分别做表单编码后再拼接和 Base64 编码。

### 5.3 公开 Authority 与 BackchannelAuthority 的区别

这是 Workbench 最容易配错的一项：

| 键 | 面向谁 | 作用 |
| --- | --- | --- |
| `Auth:Authority` | 浏览器 + 服务端 | Provider 的**公开**基础地址。用于拼授权跳转 URL、Discovery 的 issuer 校验、JWT 的 `ValidIssuer`，以及 token 校验里的 `MetadataAddress` |
| `Auth:BackchannelAuthority` | 仅服务端 | 可选的 Provider **内部**地址。当公开地址在服务端不可达（例如容器里 `localhost:5100` 指向容器自身）时，把服务端发出的请求改写到这个地址 |

要点：

- 浏览器跳转、Discovery 的 `issuer` 比对、JWT 的 issuer 都只看 `Auth:Authority`，所以它必须是浏览器能访问、且与签发令牌的 issuer 完全一致的公开地址。
- `Auth:BackchannelAuthority` 只改写**服务端**发出的 HTTP 请求，且仅当请求的 scheme、host、port 与 `Auth:Authority` 完全相同才改写（见 `admin/src/NexusAuth.Extension/OidcBackchannelHandler.cs`）。它保留路径前缀和查询串，不会改变浏览器地址，也不改变 issuer 校验值。
- 典型场景：浏览器访问 `https://sso.example.com`，容器内服务端走 `http://sso:8080`。此时 `Auth:Authority=https://sso.example.com`，`Auth:BackchannelAuthority=http://sso:8080`。
- 两个值必须都是绝对 HTTP(S) 基础地址，不能带用户信息、查询串或片段；`Auth:BackchannelAuthority` 可以留空，表示服务端直接访问公开地址。

## 6. 认证与自身 Bootstrap

### 6.1 认证流程

1. Dashboard 启动时请求 `GET /api/auth/me`（`AllowAnonymous`）。已登录返回 `isAuthenticated=true` 和最小用户信息；未登录返回 `401` 与 `isAuthenticated=false`，前端判定未登录后显示登录页。
2. 用户点击登录，前端请求 `GET /api/auth/login`。API 拉取 Host 的 Discovery 文档，生成 `state`、`nonce` 和 S256 PKCE，将 verifier 暂存服务端，返回 `authorizeUrl`。
3. 浏览器跳转到 Host 的 `/connect/authorize`。verifier、客户端密钥都不会进入浏览器。
4. Host 回调 Workbench API 的 `GET /signin-oidc`；API 用授权码 + verifier 以 `client_secret_basic` 在服务端换取令牌，按 JWKS 校验 ID Token 的签名、issuer、audience、时效和 nonce。
5. 校验通过后 API 写入 `.NexusAuth.Workbench` HttpOnly Cookie，并跳转前端 `/auth/callback`；前端再次请求 `/api/auth/me`，进入 `/dashboard`。
6. 之后的管理请求不带 `Authorization` 时走 Cookie；带 `Authorization: Bearer` 时由 policy scheme 转交 JWT Bearer 校验。

认证端点与鉴权（源码见 `admin/src/NexusAuth.Workbench.Api/Controllers/AuthController.cs`）：

| 端点 | 鉴权 | 说明 |
| --- | --- | --- |
| `GET /api/auth/config` | 匿名 | 返回可公开的 `authority`、`clientId`、`redirectUri` |
| `GET /api/auth/login` | 匿名 | 返回 `authorizeUrl` |
| `GET /signin-oidc` | 匿名 | OIDC 回调处理 |
| `GET /api/auth/me` | 匿名入口，内部执行 Workbench 默认访问策略 | 返回 `isAuthenticated`、最小用户信息和兼容字段 `isAdministrator` |
| `POST /api/auth/logout` | 需登录（`WorkbenchAuthenticatedUser`） | 清除 Cookie，可能返回 `logoutUrl`；普通用户也可退出 |

### 6.2 自身 Bootstrap 登记了什么

Workbench API 启动时由 `WorkbenchClientCredentialHostedService` 幂等地登记自己的 OAuth 客户端和资源，**不需要手工创建**。它读取 `Auth` 和 `Bootstrap` 配置：

- 依赖项（缺任意一项即启动失败）：`Auth:ClientId`、`Auth:ClientSecret`、`Auth:Audience`、`Bootstrap:ResourceName`、`Bootstrap:ResourceDisplayName`、`Bootstrap:ClientName`；非网关模式还需要 `Auth:RedirectUri`、`Auth:PostLogoutRedirectUri`。
- 注册或更新 API resource：名称取 `Bootstrap:ResourceName`，`audience` 取 `Auth:Audience`，显示名与描述取 `Bootstrap:ResourceDisplayName` / `ResourceDescription`。
- 注册或更新 OAuth 客户端：`client_id` 取 `Auth:ClientId`，名称取 `Bootstrap:ClientName`，描述取 `ClientDescription`。
- 客户端能力：非网关模式下 grant 为 `authorization_code` + `refresh_token`，`require_pkce=true`，token 端点认证为 `client_secret_basic`，scope 为静态的 `openid profile offline_access` 加上 `Auth:Audience`，回调取 `Auth:RedirectUri`、登出回调取 `Auth:PostLogoutRedirectUri`。
- 关联关系：把该客户端与该 resource 关联，使签发的 access token 带正确 `aud`。
- 密钥同步：如果数据库里的客户端密钥与 `Auth:ClientSecret` 不一致，会用配置值替换共享密钥哈希并标注来源，因此改密钥只需改配置再重启，不用手工改库。

注意：`openid`、`profile`、`offline_access` 是客户端静态 scope，不会被当作 API resource。Bootstrap 只登记 Workbench 自己的资源，其他应用和资源请在 Dashboard 里登记。

### 6.3 扩展复用（AddNexusAuth）

`admin/src/NexusAuth.Extension` 是可选复用项。其他 .NET 应用引用该扩展后调用 `AddNexusAuth(options => { ... })` 即可获得：

- OIDC Discovery 文档获取；
- PKCE 生成（`GeneratePkce`）；
- 授权码兑换（`ExchangeCodeAsync`）与刷新（`RefreshTokensAsync`）；
- ID Token 校验（`ValidateIdTokenAsync`）；
- access token introspection（`IntrospectAccessTokenAsync`）；
- `OidcBackchannelHandler` 提供的公开地址到内网地址改写。

`AddNexusAuth` 只提供协议能力和一个内存态 `IFlowStateStore`，**不会**自动添加登录端点、回调路由、Cookie 或前端页面。这些需要接入方自行实现，可参考 `AuthController` 与 `WorkbenchApiModule`。如果只想要离线 JWT 校验，另用 `AddNexusAuthJwtBearer`；它只做签名、issuer、audience、有效期和 `token_use=access_token` 校验，不查询吊销状态。

## 7. BFF 会话、Cookie、刷新与退出

### 7.1 Cookie

- 名称：`.NexusAuth.Workbench`；
- `HttpOnly=true`、`SameSite=Lax`、`SecurePolicy=SameAsRequest`（走 HTTPS 时才是 Secure）；
- Workbench API 的控制器是 `[ApiController]` 且没有自定义 401 重定向事件，未认证访问受保护接口时返回 HTTP `401`（无权限返回 `403`），不会自动 302 跳转；登录由前端在检测到 401 后显式请求 `GET /api/auth/login` 发起；
- 本地会话有效期固定为 24 小时，开启滑动续期；
- Cookie 的加密票据中包含 `access_token`、`refresh_token`、`id_token` 和 `expires_at`。票据由 ASP.NET Core Data Protection 加密并以 `HttpOnly` Cookie 发送给浏览器，因此浏览器 JavaScript 不能读取或解密，也拿不到明文令牌。

Workbench 的 24 小时会话与 Provider 登录页“几天内免登录”是两层配置，互不相同。Provider 那层由 `NEXUSAUTH_LOGIN_FLOW_REMEMBER_ME_LIFETIME_DAYS` 控制。

多副本部署必须共享并持久化 ASP.NET Core Data Protection key ring。否则一个实例签发的 Cookie 在另一个实例上无法解密，用户会随机被登出。仓库 Compose 已把 key ring 放到命名卷 `nexusauth-admin-keys`。

### 7.2 刷新与状态检查

每次 Cookie 验证（`WorkbenchCookieAuthenticationEvents.ValidatePrincipal`）都会检查保存的令牌：

- access token 距过期**超过 1 分钟**：调用 introspection，确认 token 仍 active、主体与当前用户一致、客户端是 Workbench 客户端。不满足则拒绝 Principal 并清 Cookie。
- access token 距过期**不超过 1 分钟**：用 refresh token 刷新，再对新 token introspection 复检，然后保存轮换后的 access/refresh token，并把 Cookie 会话续到新的 24 小时窗口。
- 刷新采用单飞（single-flight）合并：并发请求共享同一次刷新结果，避免同一 refresh token 被并发消费。
- token 缺失、格式错误、用户不匹配、introspection 失败或 refresh 失败：拒绝并清 Cookie，前端下次受保护请求收到 401 后回登录页。

### 7.3 退出

`POST /api/auth/logout` 的行为：

- 读取 Cookie 里的 `id_token`；
- 若 `Auth:SignOutProvider=true` 且存在 `id_token`，返回带 `id_token_hint` 和 `post_logout_redirect_uri` 的 Host `/connect/endsession` 地址；
- 清除本地 Workbench Cookie；
- 前端拿到 `logoutUrl` 后继续跳转，完成 Provider 全局登出。

只清本地 Cookie 不等于结束 Host 的全局登录。要彻底退出需要在 `logoutUrl` 上确认 Host 的登出。

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

当前实现的重要限制：`pages/dashboard/index.tsx` 里的指标卡、认证趋势、认证结果、应用排行和安全事件（`metricCards`、`authTrendData`、`authResultData`、`applicationRankData`、`securityEvents`）都是**硬编码的示例数据数组**，页面上的“刷新”只是延时后更新本地时间，**不会**调用任何统计 API。因此仪表盘不能当作实时运营统计；需要查看真实登录数据请用“应用中心 → 登录记录”（`/oauth/login-audit-management`）。

## 10. 权限边界

Workbench API 站点采用**默认拒绝**的授权边界。这些注册仅在 `WorkbenchApiModule` 中生效，不影响独立的 `NexusAuth.Host` 站点及其开放 API。Workbench 使用框架默认的 `IAuthorizationPolicyProvider` 和 `IAuthorizationService`，由 `AddWorkbenchAuthorization()` 将同一个管理访问策略同时设为默认策略和 fallback 策略：

- 没有 `[Authorize]` 或 `[AllowAnonymous]` 的端点使用 `FallbackPolicy`；
- 只有裸 `[Authorize]` 的端点使用 `DefaultPolicy`。

该策略要求用户已通过认证，并包含一个 `WorkbenchAccessRequirement`。因此，新增到 Workbench API 的控制器和 action 会默认受保护；公开入口通过显式标注 `[AllowAnonymous]` 放行；命名策略使用各自声明的规则，公开入口应逐项审查。API 匿名请求返回 401；已登录但未通过访问评估的请求返回 403。Swagger 也受该策略保护；BFF 模式的匿名浏览器请求会跳转登录，Bearer 请求需要具备管理访问权限。不存在的路径同样先经过 fallback，只有通过授权后才返回 404。

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

Bearer 在 BFF 和网关模式下都校验 issuer、audience、有效期及 `token_use=access_token`，随后要求 `client_id` 属于允许列表，再调用 `IUserTokenValidator.IsActiveAsync` 检查用户和吊销状态。默认允许归一化后的 `Auth:ClientId`；其他调用方必须显式加入 `Auth:AllowedBearerClientIds` 数组，例如：

```json
{
  "Auth": {
    "AllowedBearerClientIds": ["my-gateway-client"]
  }
}
```

也可设置标准 .NET 环境变量 `Auth__AllowedBearerClientIds__0=my-gateway-client`。缺少或不匹配的 `client_id` 返回 401；允许列表不会授予普通用户管理权限。

## 11. 网关模式边界

设置 `NEXUSAUTH_WORKBENCH_GATEWAY_ENABLED=true`（配置键 `Gateway:Enabled`）后，Workbench API 切换到网关模式：

- 不再注册 Cookie 与本地登录方案，默认认证方案变为 `Workbench.Bearer`，只接受网关转发的 `Authorization: Bearer <access_token>`。
- `AuthController` 会从控制器特性里移除，因此 `/api/auth/config`、`/api/auth/login`、`/signin-oidc`、`/api/auth/logout` 不再发布；通过默认授权的请求返回 404，匿名请求会先被 fallback 拦截并返回 401（实现见 `admin/src/NexusAuth.Workbench.Api/GatewayControllerFeatureProvider.cs`）。
- `/api/auth/me` 仍在，但只反映 Bearer 身份，并按同一 Workbench 默认访问策略计算兼容字段 `isAdministrator`。
- 自身 Bootstrap 仍会登记资源、客户端和密钥，但网关模式下客户端不开放 grant、登录回调或登出回调（`grantTypes`、`redirectUris`、`postLogoutRedirectUris` 为空），避免和网关登录客户端冲突。
- `Auth:RedirectUri`、`Auth:PostLogoutRedirectUri`、`Auth:Scope` 在网关模式下不再是必填；`Auth:Authority`、`Auth:Audience`、`Auth:ClientId`、`Auth:ClientSecret` 和 Bootstrap 名称仍需配置。

网关必须自己负责浏览器登录、回调、刷新和登出，并把 access token 以 Bearer 形式转发给 Workbench API。根目录 Compose 里的 Dashboard 仍然是 BFF 登录模式，**不会**随这个开关自动切换到网关登录；只改 API 配置会让 Dashboard 无法使用原有登录端点。网关登录客户端需要另行在 Dashboard 创建，不能复用 Workbench 默认客户端；同时将其 ID 加入 `Auth:AllowedBearerClientIds`。切换回非网关模式会恢复默认客户端的授权码和刷新配置。

APISIX 相关对接见 [APISIX 网关对接手册](13-APISIX网关对接手册.md)。

## 12. 构建与部署

### 12.1 构建

从仓库根目录执行：

```bash
dotnet build admin/NexusAuth.Admin.sln
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run build
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run lint
```

Dashboard 的 `npm run build` 先跑 `tsc -b` 再跑 Vite build，没有单独的 `npm run typecheck` 脚本，类型检查含在 build 里。Workbench API 目标框架是 `net10.0`。

### 12.2 镜像与端口

- Workbench API 镜像：`admin/src/NexusAuth.Workbench.Api/Dockerfile`，SDK 10 构建、`aspnet:10.0` 运行，监听 8080。
- Dashboard 镜像：`admin/src/NexusAuth.Workbench.Dashboard/Dockerfile`，Node 22 构建、Nginx 1.27 托管，监听 80。
- 根 Compose 映射：`dashboard` → 宿主 `5560`；`db` → 宿主 `33210`；`sso` → 宿主 `5100`；`admin-api` 不映射宿主端口。

### 12.3 生产要点

- Dashboard 对外只暴露一个域名，由反向代理把 `/api` 和 `/signin-oidc` 转发到 Workbench API，保留 `Host`、`X-Forwarded-Proto`、`X-Forwarded-Host` 和 Cookie。
- Provider 登记的 `redirect_uri`、`post_logout_redirect_uri` 与 API 配置逐字符一致，且使用 HTTPS。
- `Auth:Authority` 用公网 HTTPS 地址并与 token 的 `iss` 一致；服务端不可直达公开地址时用 `Auth:BackchannelAuthority` 指向内网。
- `Auth:Audience` 与 Workbench API resource 的 audience 一致。
- 多副本共享 Data Protection key ring，数据库使用持久卷并备份。
- 不提交或在镜像里固化真实 `ClientSecret`；不使用仓库内置的开发密钥；生产不执行 `demo/seed.sql`。

## 13. 排错

| 现象 | 可能原因 | 处理 |
| --- | --- | --- |
| API 启动即退出，日志出现 `Auth:... is required` | 认证配置或 Bootstrap 名称缺失 | 补齐第 5.2 节的必填项 |
| API 启动失败，提示数据库相关错误 | schema 未初始化或连接串不对 | 执行 `production-init.sql`；核对连接串与端口 |
| 浏览器登录后立刻跳回登录页 | 回调地址、Issuer 或 Cookie 域不一致 | 核对第 4.4 节的四处地址，确认 `Authority` 与 Host issuer 一致 |
| 本地 Dashboard 请求失败，命中内网地址 | Vite 代理仍是 `192.168.31.200:9100` | 把 `vite.config.ts` 的 `/api`、`/signin-oidc` 目标改成实际 API |
| API 报无法访问 Provider Discovery | 服务端访问不到公开 Authority | 配置 `Auth:BackchannelAuthority` 指向内部地址 |
| Bearer 调用返回 401 | audience/issuer/client_id 不匹配、token 过期或不是 access_token | 核对 audience、issuer、`token_use` 与客户端允许列表 |
| 管理接口返回 403 | 当前访问授权 handler 未通过 | 默认授权 handler 下用启用状态的系统账号登录；替换授权 handler 时核对其判定（见第 10 节） |
| 多实例下用户随机被登出 | Data Protection key ring 未共享 | 共享并持久化 key ring |
| 网关模式后 `/api/auth/login` 返回 404 | 这是网关模式的预期行为 | 由网关实现登录，或关闭网关模式 |
| 无法查看已创建的 SCIM/开放 API 凭证明文 | 明文只在创建时返回一次 | 重新创建凭证并立即保存 |

Host 侧的问题（登录页、邮件、Passkey、令牌签发、SCIM 服务端行为）请看 [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) 和 [SMTP 配置与发信验证](SMTP发信试验.md)。

## 14. 相关文档

- [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md)：独立身份认证服务器的启动、配置与协议接入。
- [Workbench API 统一登录接入模板](Workbench-API统一登录接入模板.md)：服务端接入样例。
- [Workbench Dashboard 统一登录接入模板](Workbench-Dashboard统一登录接入模板.md)：前端登录样例。
- [OAuth/OIDC 协议设计](11-OAuth-OIDC协议设计.md)：端点、参数校验和令牌规则。
- [APISIX 网关对接手册](13-APISIX网关对接手册.md)：外置网关认证模式与信任边界。
- [SMTP 配置与发信验证](SMTP发信试验.md)：Host 邮件配置与发信排错。
- [Workbench API README](../admin/README.md)：组件入口。
- [Dashboard README](../admin/src/NexusAuth.Workbench.Dashboard/README.md)：前端入口。
