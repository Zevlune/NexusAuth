# NexusAuth.Host 使用手册

本文只说明 NexusAuth.Host，也就是 NexusAuth 的独立身份认证服务。本文按当前仓库源码整理，不把 Workbench 或 Dashboard 的启动、菜单和管理 API 混入 Host 流程。

NexusAuth.Host 负责：

- OAuth 2.1 / OpenID Connect 协议端点与令牌签发；
- 登录页、授权确认页、设备授权页和账号安全中心；
- 用户、密码、TOTP、Passkey、邮箱验证码、登录审计与会话；
- 面向外部系统的 SCIM 2.0 用户供给接口；
- 面向外部机器消费者的只读 Open API。

管理工作台是另一套应用：Workbench API + Dashboard。需要管理用户、客户端、API 资源或凭证时，见 [Workbench 使用手册](Workbench使用手册.md)。Host 自身没有用于创建客户端、API 资源、SCIM 凭证或 Open API 凭证的管理端点。

阅读导航：

- [1. 与 Workbench / Dashboard 的边界](#1-与-workbench--dashboard-的边界)
- [2. 启动 Host](#2-启动-host)
- [3. 配置](#3-配置)
- [4. 管理员初始化](#4-管理员初始化)
- [5. 用户功能](#5-用户功能)
- [6. OAuth / OIDC 端点](#6-oauth--oidc-端点)
- [7. SCIM 2.0 用户供给](#7-scim-20-用户供给)
- [8. 只读 Open API](#8-只读-open-api)
- [9. 限流和安全策略](#9-限流和安全策略)
- [10. 排错](#10-排错)
- [11. 生产检查清单](#11-生产检查清单)
- [12. 源码核对索引](#12-源码核对索引)
- [13. 已知限制](#13-已知限制)

## 1. 与 Workbench / Dashboard 的边界

| 能力 | NexusAuth.Host | Workbench API + Dashboard |
| --- | --- | --- |
| 身份认证、登录、令牌签发 | 提供 | 不提供 |
| 账号安全中心 | 提供 `/account/*` | 不提供 |
| 客户端和 API 资源管理 | 不提供 Host 管理端点 | 管理入口 |
| SCIM / Open API 凭证生命周期 | 校验调用凭证，提供 SCIM 用户供给与 Open API 查询 | 创建、更新与撤销的管理入口 |
| 部署关系 | 可以单独运行 | 依赖 Host 和共享数据库 |

Host 可以在不启动 Workbench API、Dashboard 的情况下运行。Host 需要数据库，但不需要 Workbench。

如果需要登记 OAuth 客户端、API 资源、SCIM 凭证或 Open API 凭证，可以采用以下任一方式：

1. 启动 Workbench，通过管理界面登记；使用方式见 [Workbench 使用手册](Workbench使用手册.md)。
2. 在受控的内部系统中复用仓库的 `NexusAuth.Application` 和 `NexusAuth.Persistence`，调用对应的应用服务或仓储写入数据。

不要把 `openapi/v1/*` 或 `scim/v2/*` 当成 Host 的管理接口。它们是给外部消费者读取目录或供给用户的下游接口。

## 2. 启动 Host

### 2.1 环境要求

- .NET SDK 10，项目目标框架是 `net10.0`，见 `Directory.Build.props`。
- PostgreSQL 16 或兼容版本。仓库的 Compose 使用 `postgres:16-alpine`。
- 做邮箱验证码登录、邮箱绑定和找回密码时，需要可发信的 SMTP。
- 做 Passkey 登录或注册时，浏览器访问的域名、Origin 必须与 WebAuthn 配置一致。

Host 镜像构建见 [`src/NexusAuth.Host/Dockerfile`](../src/NexusAuth.Host/Dockerfile)，运行端口为容器内的 `8080`。

### 2.2 只启动 db 和 sso 的 Compose 流程

仓库根目录的 [`docker-compose.yml`](../docker-compose.yml) 同时定义了 `db`、`sso`、`admin-api` 和 `dashboard`。只启动 Host 所需的数据库和认证服务时，要显式指定 `db` 和 `sso`，并先处理 SMTP 启动前提。当前镜像内的 `Smtp:Enabled` 默认为 `true`，而仓库没有默认 SMTP 密码；如果容器中没有可用的完整 SMTP 配置，Host 会在启动校验阶段失败。启动前二选一：

1. 按 [Host SMTP 配置与发信验证](SMTP发信试验.md) 配置完整的可用 SMTP；
2. 按该专题创建本地 `docker-compose.smtp.local.yml`，至少包含：

```yaml
services:
  sso:
    environment:
      NEXUSAUTH_SMTP_ENABLED: "false"
```

随后使用显式两个 `-f` 参数启动：

```bash
docker compose -f docker-compose.yml -f docker-compose.smtp.local.yml up -d db sso
```

需要重新构建 Host 镜像时：

```bash
docker compose -f docker-compose.yml -f docker-compose.smtp.local.yml up -d --build db sso
```

查看日志：

```bash
docker compose -f docker-compose.yml -f docker-compose.smtp.local.yml logs -f sso
```

Compose 默认把 SSO 暴露在本机回环地址：

- SSO：`http://localhost:5100`
- PostgreSQL：`127.0.0.1:33210`

Compose 的 `sso` 默认会启用自注册和 Passkey，并使用一个开发用 Bootstrap 管理员默认值。生产或共享环境必须覆盖这些值。数据库、签名材料和 Data Protection key ring 使用命名卷持久化。

### 2.3 数据库初始化和迁移边界

Host 不会自动创建数据库 schema，也不会在启动时执行 EF Core migration。`NexusAuth.Persistence` 只注册并连接 DbContext，见 [`src/NexusAuth.Persistence/EntityFrameworkCoreModule.cs`](../src/NexusAuth.Persistence/EntityFrameworkCoreModule.cs)。

根 Compose 的数据库初始化由两段组成：

1. [`docker/postgres-init.sh`](../docker/postgres-init.sh) 在全新数据卷首次启动时创建 `nexusauth` 数据库，然后对它执行 [`production-init.sql`](../production-init.sql)。
2. [`production-init.sql`](../production-init.sql) 创建 `nexusauth` schema、表和索引。它是当前新数据库的最终 schema，不包含历史兼容迁移。

因此：

- `production-init.sql` 应连接到实际的 `nexusauth` 数据库执行，而不是只连接到 `postgres` 数据库后假设应用会自动切换。
- PostgreSQL 的 `/docker-entrypoint-initdb.d` 脚本只在数据卷第一次初始化时运行。已有数据卷不会因为重启容器而补执行。
- 已有数据库需要补表、改表时，必须由部署方准备并执行迁移；Host 不会自动迁移。
- 不要依赖 `docker/local-demo-seed.sql`。它只属于显式启用的本地预览扩展，不是 Host 启动的前提。

手工执行当前 schema 时，可以用等价命令：

```bash
docker compose exec -T db \
  psql --username nexusauth --dbname nexusauth \
  --set ON_ERROR_STOP=1 \
  --file /opt/nexusauth-init/production-init.sql
```

对已有数据库执行前，先在可恢复的副本或备份上确认目标库、当前 schema 和幂等性。`production-init.sql` 对当前新库使用 `CREATE SCHEMA IF NOT EXISTS` 和 `CREATE TABLE`，它不是通用升级脚本。

### 2.4 从源码运行 Host

先只启动数据库：

```bash
docker compose up -d db
```

开发环境配置在 [`src/NexusAuth.Host/appsettings.Development.json`](../src/NexusAuth.Host/appsettings.Development.json) 中指向 `127.0.0.1:33210`。使用的启动配置见 [`src/NexusAuth.Host/Properties/launchSettings.json`](../src/NexusAuth.Host/Properties/launchSettings.json)。

当前源码的默认 SMTP 配置有一个重要启动约束：`Smtp:Enabled` 为 `true`，但仓库默认没有 `Smtp:Password`。SMTP 的 `ValidateOnStart` 会因此阻止 Host 启动。无邮件环境可以先关闭 SMTP：

```bash
ASPNETCORE_ENVIRONMENT=Development \
NEXUSAUTH_SMTP_ENABLED=false \
NEXUSAUTH_BOOTSTRAP_ADMIN_USERNAME=admin \
NEXUSAUTH_BOOTSTRAP_ADMIN_PASSWORD='replace-with-a-strong-password' \
dotnet run --project src/NexusAuth.Host/NexusAuth.Host.csproj --launch-profile NexusAuth.Host
```

启动后访问：

- 登录页：`http://localhost:5100/account/login`
- 账号中心：`http://localhost:5100/account`
- 设备授权页：`http://localhost:5100/device`
- Swagger UI：`http://localhost:5100/swagger`
- OpenID Discovery：`http://localhost:5100/.well-known/openid-configuration`

如果不使用 launch profile，需要自己补齐环境变量，尤其要避免意外以 Production 默认值启动：

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5100 \
NEXUSAUTH_CONNECTION_STRINGS_DEFAULT='Host=127.0.0.1;Port=33210;Database=nexusauth;Username=nexusauth;Password=nexusauth_dev_password;Search Path=nexusauth' \
NEXUSAUTH_JWT_ISSUER=http://localhost:5100 \
NEXUSAUTH_JWT_SIGNING_MODE=Certificate \
NEXUSAUTH_JWT_SIGNING_PASSWORD='replace-with-a-development-only-password' \
NEXUSAUTH_SMTP_ENABLED=false \
NEXUSAUTH_BOOTSTRAP_ADMIN_USERNAME=admin \
NEXUSAUTH_BOOTSTRAP_ADMIN_PASSWORD='replace-with-a-strong-password' \
dotnet run --project src/NexusAuth.Host/NexusAuth.Host.csproj --no-launch-profile
```

## 3. 配置

### 3.1 配置来源与优先级

Host 使用标准 .NET 配置系统，主要来源为：

1. [`src/NexusAuth.Host/appsettings.json`](../src/NexusAuth.Host/appsettings.json)
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json`
3. 环境变量
4. 命令行参数
5. Host 在 [`src/NexusAuth.Host/Program.cs`](../src/NexusAuth.Host/Program.cs) 中额外支持的少量单下划线环境变量映射

单下划线变量不是 .NET 的通用语法，只是 Host 显式映射的兼容入口。完整配置能力始终可以通过标准双下划线环境变量使用，例如：

```bash
Smtp__Password=...
Jwt__SigningKeys__0__Source=Certificate
Jwt__SigningKeys__0__IsActive=true
Jwt__SigningKeys__0__Path=/run/secrets/current.pfx
```

标准双下划线名称可以与仓库配置节一一对应。`Jwt:SigningKeys` 这类数组或多级对象没有全部列入单下划线白名单，应使用 `Jwt__SigningKeys__...`。

### 3.2 默认配置差异

以下差异直接取自当前源码：

| 配置 | `appsettings.json` | `appsettings.Development.json` | launch profile / Compose |
| --- | --- | --- | --- |
| `ConnectionStrings:Default` | `localhost:5432`，示例密码 | `127.0.0.1:33210`，开发库 | Compose 通过 `NEXUSAUTH_CONNECTION_STRINGS_DEFAULT` 覆盖到 `db:5432` |
| `Jwt:Issuer` | `http://localhost:5100` | 不覆盖 | Compose 默认 `http://localhost:5100` |
| `Jwt:SigningMode` | `Certificate` | `Certificate` | Host/Compose 默认 `Certificate` |
| `Jwt:SigningPassword` | 未设置 | `nexusauth-development` | Compose 默认 `nexusauth-compose-dev` |
| `Smtp:Enabled` | `true` | 不覆盖 | Compose 不显式传 `Enabled`；默认沿用 `true` |
| `Smtp:Password` | 未设置 | 不覆盖 | Compose 只透传 `NEXUSAUTH_SMTP_PASSWORD` |
| `SelfRegistration:Enabled` | `false` | 不覆盖 | launch profile 和 Compose 默认 `true` |
| `WebAuthn:Enabled` | `false` | 不覆盖 | launch profile 和 Compose 默认 `true` |
| `WebAuthn:Origins` | `http://localhost:5200` | 不覆盖 | launch profile 和 Compose 默认 `http://localhost:5100` |
| Bootstrap 管理员 | 用户名为空 | 不覆盖 | Compose 有开发默认值，必须覆盖 |

示例配置中的数据库密码、签名密码和 Bootstrap 密码都只适合本地开发。生产环境必须通过 Secret 或受控环境变量覆盖。

### 3.3 Host 支持的单下划线环境变量

以下为 Host 显式映射的主要变量。未列出的配置项请使用标准双下划线名称，或者修改对应的 JSON 配置文件。

| 环境变量 | 配置键 |
| --- | --- |
| `NEXUSAUTH_CONNECTION_STRINGS_DEFAULT` | `ConnectionStrings:Default` |
| `NEXUSAUTH_SMTP_ENABLED` | `Smtp:Enabled` |
| `NEXUSAUTH_SMTP_HOST` | `Smtp:Host` |
| `NEXUSAUTH_SMTP_PORT` | `Smtp:Port` |
| `NEXUSAUTH_SMTP_SECURITY` | `Smtp:Security` |
| `NEXUSAUTH_SMTP_USERNAME` | `Smtp:Username` |
| `NEXUSAUTH_SMTP_PASSWORD` | `Smtp:Password` |
| `NEXUSAUTH_SMTP_FROM_ADDRESS` | `Smtp:FromAddress` |
| `NEXUSAUTH_SMTP_FROM_NAME` | `Smtp:FromName` |
| `NEXUSAUTH_SMTP_VERIFICATION_CODE_COOLDOWN_SECONDS` | `Smtp:VerificationCodeCooldownSeconds` |
| `NEXUSAUTH_SMTP_VERIFICATION_CODE_LIFETIME_MINUTES` | `Smtp:VerificationCodeLifetimeMinutes` |
| `NEXUSAUTH_JWT_ISSUER` | `Jwt:Issuer` |
| `NEXUSAUTH_JWT_SIGNING_MODE` | `Jwt:SigningMode` |
| `NEXUSAUTH_JWT_SIGNING_PATH` | `Jwt:SigningPath` |
| `NEXUSAUTH_JWT_SIGNING_PASSWORD` | `Jwt:SigningPassword` |
| `NEXUSAUTH_JWT_DEVELOPMENT_SIGNING_CERTIFICATE_PATH` | `Jwt:DevelopmentSigningCertificatePath` |
| `NEXUSAUTH_JWT_DEVELOPMENT_SIGNING_CERTIFICATE_PASSWORD` | `Jwt:DevelopmentSigningCertificatePassword` |
| `NEXUSAUTH_JWT_SIGNING_CERTIFICATE_PATH` | `Jwt:SigningCertificatePath` |
| `NEXUSAUTH_JWT_SIGNING_CERTIFICATE_PASSWORD` | `Jwt:SigningCertificatePassword` |
| `NEXUSAUTH_JWT_DEVELOPMENT_SIGNING_KEY_PATH` | `Jwt:DevelopmentSigningKeyPath` |
| `NEXUSAUTH_JWT_SIGNING_KEY_PATH` | `Jwt:SigningKeyPath` |
| `NEXUSAUTH_JWT_ACCESS_TOKEN_LIFETIME_MINUTES` | `Jwt:AccessTokenLifetimeMinutes` |
| `NEXUSAUTH_JWT_REFRESH_TOKEN_LIFETIME_MINUTES` | `Jwt:RefreshTokenLifetimeMinutes` |
| `NEXUSAUTH_SELF_REGISTRATION_ENABLED` | `SelfRegistration:Enabled` |
| `NEXUSAUTH_WEBAUTHN_ENABLED` | `WebAuthn:Enabled` |
| `NEXUSAUTH_WEBAUTHN_RP_ID` | `WebAuthn:RelyingPartyId` |
| `NEXUSAUTH_WEBAUTHN_RP_NAME` | `WebAuthn:RelyingPartyName` |
| `NEXUSAUTH_WEBAUTHN_ORIGIN` | `WebAuthn:Origins:0` |
| `NEXUSAUTH_WEBAUTHN_CHALLENGE_LIFETIME_SECONDS` | `WebAuthn:ChallengeLifetimeSeconds` |
| `NEXUSAUTH_BOOTSTRAP_ADMIN_USERNAME` | `BootstrapAdmin:Username` |
| `NEXUSAUTH_BOOTSTRAP_ADMIN_PASSWORD` | `BootstrapAdmin:Password` |
| `NEXUSAUTH_BOOTSTRAP_ADMIN_NICKNAME` | `BootstrapAdmin:Nickname` |
| `NEXUSAUTH_BOOTSTRAP_ADMIN_EMAIL` | `BootstrapAdmin:Email` |
| `NEXUSAUTH_CLIENT_IP_FORWARDED_HEADERS_ENABLED` | `ClientIp:ForwardedHeadersEnabled` |
| `NEXUSAUTH_CLIENT_IP_KNOWN_PROXIES` | `ClientIp:KnownProxies` |
| `NEXUSAUTH_CLIENT_IP_KNOWN_NETWORKS` | `ClientIp:KnownNetworks` |
| `NEXUSAUTH_CLIENT_IP_FORWARD_LIMIT` | `ClientIp:ForwardLimit` |

登录页、滑块验证码、日志和 TOTP 也有一组单下划线映射；完整列表见 [`Program.cs`](../src/NexusAuth.Host/Program.cs)。

### 3.4 SMTP 与验证码

SMTP 配置在 [`src/NexusAuth.Shared/Email/SmtpOptions.cs`](../src/NexusAuth.Shared/Email/SmtpOptions.cs)，校验规则在 `SmtpOptionsValidator`。启用 SMTP 时必须满足：

- `Host` 非空；
- `Port` 在 1 到 65535；
- `Security` 只能是 `SslOnConnect` 或 `StartTls`；
- `Username` 和 `Password` 非空；
- `FromAddress` 是单个有效邮箱地址；
- 验证码冷却为 1 到 3600 秒；
- 验证码有效期为 1 到 60 分钟；
- 冷却时间不得超过验证码有效期。

当前 `appsettings.json` 把 `Smtp:Enabled` 设为 `true`，但没有提供 `Smtp:Password`。因此无论是否使用邮箱功能，默认直接启动都会在选项校验阶段失败。可以选择：

```bash
export NEXUSAUTH_SMTP_ENABLED=false
```

或完整提供 SMTP 密码：

```bash
export NEXUSAUTH_SMTP_PASSWORD='smtp-authorization-code'
```

Compose 的 SMTP 注入范围很窄。根 [`docker-compose.yml`](../docker-compose.yml) 只给 `sso` 容器显式转发了 `NEXUSAUTH_SMTP_PASSWORD`：

```yaml
NEXUSAUTH_SMTP_PASSWORD: ${NEXUSAUTH_SMTP_PASSWORD:-}
```

因此：

- 在宿主 `.env` 中设置 `NEXUSAUTH_SMTP_PASSWORD`，可以通过 Compose 插值进入容器；
- 在宿主 `.env` 中设置 `NEXUSAUTH_SMTP_ENABLED=false`、`NEXUSAUTH_SMTP_HOST=...` 等变量，不会自动进入 `sso` 容器，因为没有写进该服务的 `environment`；
- 如果要在 Compose 中关闭 SMTP 或改 Host、端口、发件人，需要在 Compose override 或服务配置中显式增加相应环境变量，或修改镜像内使用的 `appsettings.json` 后重建镜像。

标准双下划线变量如 `Smtp__Enabled=false` 也只有在它们实际传入容器环境时才生效。

邮箱验证码的行为由 [`src/NexusAuth.Application/Services/Email/EmailVerificationCodeService.cs`](../src/NexusAuth.Application/Services/Email/EmailVerificationCodeService.cs) 定义：

- 验证码为 6 位数字；
- 同一用途、邮箱和账号密码版本只能保留一个有效验证码；
- 默认冷却 60 秒，默认有效期 5 分钟；
- 每个邮箱每小时最多发送 10 封；
- 单个验证码最多允许 5 次错误尝试；
- 验证码和限流状态存放在进程内存中，重启会丢失；多实例部署当前不能共享这些状态。

### 3.5 JWT 签名和密钥轮换

签名配置实体见 [`src/NexusAuth.Application/Services/Tokens/JwtOptions.cs`](../src/NexusAuth.Application/Services/Tokens/JwtOptions.cs)，加载逻辑见 [`TokenSigningCredentialsProvider.cs`](../src/NexusAuth.Application/Services/Tokens/TokenSigningCredentialsProvider.cs)。

支持两种签名源：

- `Certificate`：证书文件，支持 `.pfx` / `.p12` 或证书文件；RSA 至少 2048 位；活跃证书必须有私钥；如果证书带 KeyUsage 扩展，该扩展必须包含 `DigitalSignature`；只有活跃证书会校验当前有效期。
- `RsaKeyFile`：JSON 密钥文件，包含 `KeyId` 和 base64 编码的私钥；RSA 至少 2048 位。

签名密钥的核心规则：

- 所有已加载密钥中必须恰好有一个 `IsActive=true`；
- 活跃密钥必须有可签名私钥；
- 所有已加载密钥都会出现在 JWKS，用于验签；
- 密钥 ID 不能重复；
- 生产环境不会自动生成缺失的证书或密钥文件；开发环境在配置允许时可以为活跃密钥自动创建。

旧式单密钥配置可以继续使用：

```json
{
  "Jwt": {
    "Issuer": "https://sso.example.com",
    "SigningMode": "Certificate",
    "SigningPath": "/run/secrets/nexusauth-signing.pfx",
    "SigningPassword": "replace-from-secret",
    "AccessTokenLifetimeMinutes": 60,
    "RefreshTokenLifetimeMinutes": 43200
  }
}
```

生产环境推荐用 `SigningKeys` 数组显式配置轮换。示例：

```json
{
  "Jwt": {
    "Issuer": "https://sso.example.com",
    "SigningKeys": [
      {
        "Source": "Certificate",
        "IsActive": false,
        "KeyId": "old-key",
        "Path": "/run/secrets/old-signing.pfx",
        "Password": "old-secret"
      },
      {
        "Source": "Certificate",
        "IsActive": true,
        "KeyId": "new-key",
        "Path": "/run/secrets/new-signing.pfx",
        "Password": "new-secret"
      }
    ]
  }
}
```

等价的标准双下划线环境变量如下：

```bash
Jwt__Issuer=https://sso.example.com
Jwt__SigningKeys__0__Source=Certificate
Jwt__SigningKeys__0__IsActive=false
Jwt__SigningKeys__0__Path=/run/secrets/old-signing.pfx
Jwt__SigningKeys__0__Password=...
Jwt__SigningKeys__1__Source=Certificate
Jwt__SigningKeys__1__IsActive=true
Jwt__SigningKeys__1__Path=/run/secrets/new-signing.pfx
Jwt__SigningKeys__1__Password=...
```

轮换步骤：

1. 先把旧密钥设为非活跃、新密钥设为活跃，重启或滚动重启 Host。
2. 确认 `/.well-known/jwks.json` 同时返回新旧公钥。
3. 等待客户端刷新 JWKS 缓存，并等待旧 access token / ID token 超过最大有效期。默认 access token 和 ID token 都是 60 分钟。
4. 再移除旧密钥。

如果使用单键配置，直接替换文件会导致仍在有效期内的旧令牌无法验签或自省。

### 3.6 Data Protection

Host 使用 ASP.NET Core Data Protection 保护：

- 登录 Cookie；
- 防跨站请求伪造令牌；
- 登录流程中的短期状态；
- TOTP secret；
- Passkey 注册和登录挑战状态；
- 滑块验证码挑战。

仓库没有在代码中注册自定义 Data Protection 持久化提供程序。容器部署把默认 key ring 挂载为 `/root/.aspnet/DataProtection-Keys`：

```yaml
- nexusauth-sso-keys:/root/.aspnet/DataProtection-Keys
```

生产要求：

- 持久化 key ring，不要在每次发布时清空；
- 多副本部署共享同一 key ring，否则一个实例无法解开另一个实例的 Cookie 或受保护状态；
- 备份 key ring，并把备份当作敏感材料；
- 丢失 key ring 会导致旧 Cookie 失效、TOTP secret 无法解密、Passkey 流程状态失效。

### 3.7 日志

Host 使用 `Luck.Logging.Serilog`，配置节为 `LuckLogging`，默认写入 `logs/nexusauth-sso-.log`。根 Compose 把 `/app/logs` 映射到宿主机 `./logs/sso`。

默认日志参数包括：

- 最低级别 `Information`；
- `Microsoft`、`Npgsql`、EF Core 命令等默认降到 `Warning`；
- 单文件上限 100 MiB；
- 保留 30 个文件；
- 按大小滚动；
- 启用共享写入；
- 每秒刷新。

可用单下划线变量包括 `NEXUSAUTH_LUCK_LOGGING_FILE_PATH`、`NEXUSAUTH_LUCK_LOGGING_MINIMUM_LEVEL` 等；其他日志配置可使用 `LuckLogging__...`。

### 3.8 反向代理与客户端 IP

默认情况下，Host 不处理 `X-Forwarded-*`。只有显式启用并配置可信代理时才接受转发头，见 [`src/NexusAuth.Host/Authentication/ClientIpConfiguration.cs`](../src/NexusAuth.Host/Authentication/ClientIpConfiguration.cs)。

```bash
NEXUSAUTH_CLIENT_IP_FORWARDED_HEADERS_ENABLED=true
NEXUSAUTH_CLIENT_IP_KNOWN_PROXIES=10.10.0.8
NEXUSAUTH_CLIENT_IP_KNOWN_NETWORKS=
NEXUSAUTH_CLIENT_IP_FORWARD_LIMIT=1
```

约束：

- `ForwardLimit` 必须在 1 到 10 之间；
- 开启转发头时必须至少配置可信代理或可信网段；
- 只配置实际代理 IP 或专属代理网段，不要信任整个公网；
- 代理入口应覆盖客户端提交的 `X-Forwarded-For` 和 `X-Forwarded-Proto`；
- 多级代理要逐层配置可信节点和跳数；
- 审计和限流使用的是处理后的客户端地址。

生产环境还应让 `Jwt:Issuer` 和 Discovery 使用对外 HTTPS 地址。`Jwt:Issuer` 直接决定 Discovery 中的 issuer 和 token 的 `iss`。

仓库的 APISIX 本地 backchannel 路由在 [`docker/apisix/backchannel-route.json`](../docker/apisix/backchannel-route.json) 中使用 `/*`，把 `5100` 入口的请求原样转发到 `sso:8080`。如果由其他网关暴露 Host，也应保留 ASP.NET 路由的精确路径：

- `/.well-known/openid-configuration` 和 `/.well-known/jwks.json`；
- `/connect/authorize`、`/connect/token`、`/connect/userinfo`、`/connect/introspect`、`/connect/revocation`、`/connect/deviceauthorization`、`/connect/endsession`；
- `/account/*` 和 `/device`；
- `/scim/v2/*`；
- `/openapi/v1/*`；
- 需要暴露 Swagger 时，`/swagger` 和 `/swagger/v1/swagger.json`。

不要在转发时自动剥掉 `/scim/v2` 或 `/openapi/v1` 前缀。网关还应保留或正确覆盖 `Host`、`X-Forwarded-Proto`，使客户端断言的 endpoint audience、Discovery 地址和登录回调保持一致。完整 APISIX 接入另见 [APISIX 网关对接手册](13-APISIX网关对接手册.md)。

## 4. 管理员初始化

管理员初始化由 [`src/NexusAuth.Host/BootstrapAdminHostedService.cs`](../src/NexusAuth.Host/BootstrapAdminHostedService.cs) 完成。

关键行为：

- `BootstrapAdmin:Username` 和 `BootstrapAdmin:Password` 始终是必填项，必须在配置中同时提供；
- 用户名或密码任一为空，Host 启动失败；
- 如果用户名已存在，Host 保留现有密码，只会在必要时把用户标记为系统账号；
- 如果用户名不存在，Host 使用配置中的密码创建管理员；
- 配置中的密码只在创建时使用，后续修改配置不会自动重置已有管理员的密码；
- 系统账号不能被 SCIM 修改。

Bootstrap 配置示例：

```json
{
  "BootstrapAdmin": {
    "Username": "admin",
    "Password": "replace-from-secret",
    "Nickname": "System Admin",
    "Email": "admin@example.com"
  }
}
```

生产环境不要使用 Compose 中的默认管理员密码。

## 5. 用户功能

### 5.1 登录

登录页是 `/account/login`。账号标识可以是用户名、邮箱或手机号：

- 先按用户名查找；
- 如果标识包含 `@`，再按邮箱查找；
- 最后按手机号查找。

默认登录流程在 `LoginFlow:Steps` 中定义：

1. `password`，`required`；
2. `totp`，`conditional`。

TOTP 为 `conditional` 时，只有账号已经启用 TOTP 才要求第二因子。TOTP 为 `required` 时，未配置 TOTP 的账号无法登录，并且账号中心不允许关闭 TOTP。

登录成功后会创建 SSO session，并写入名为 `.NexusAuth.Identity` 的 Cookie。Cookie 默认 HttpOnly、SameSite=Lax，`SecurePolicy=SameAsRequest`。更完整的会话行为由 `LoginFlow` 和数据库中的 SSO session 共同决定。

`LoginFlow` 默认值：

- 待完成 TOTP 等中间状态的签名有效期为 5 分钟；
- 普通 SSO session 为 1440 分钟，即 24 小时；
- “记住我”为 3 天；
- 允许使用“记住我”。

Cookie 每次请求都会检查数据库中的 SSO session 是否仍有效；用户被停用或 session 被撤销时，现有 Cookie 会被拒绝。

登录失败会按 `Security:LoginFailureLimit` 和 `Security:LoginLockoutMinutes` 锁定账号，默认是 5 次失败、锁定 5 分钟。锁定信息持久化在用户记录中。

密码登录、邮箱验证码登录、TOTP 和 Passkey 都受 Host 限流和账号策略影响。

### 5.2 自注册

自注册页是 `/account/register`，默认关闭，只有 `SelfRegistration:Enabled=true` 时才可用。launch profile 和根 Compose 默认开启。

启用后，注册表单要求：

- `Username`：3 到 64 个字符，只允许字母、数字、点、下划线和连字符；
- `Nickname`：2 到 64 个字符；
- `Email`：必须是有效邮箱，最多 256 个字符；
- `Password`：8 到 128 个字符；
- 两次密码必须一致。

注册成功后，Host 直接建立登录会话。注册流程本身不会发送邮箱验证码，也不会先验证邮箱所有权；表单填写的邮箱会在注册时直接写入用户记录。邮箱验证码用于后续的邮箱绑定、更换和验证码登录，不要把“自注册成功”理解为“邮箱已经通过验证”。

如果启用 Passkey，注册成功后默认引导到 Passkey 建档页；用户可以跳过。

### 5.3 邮箱绑定、验证和密码找回

账号需要先完成登录，再进入 `/account/email` 操作。

绑定或更换邮箱的流程：

1. 输入新邮箱；
2. 输入当前密码；
3. 如果账号已启用 TOTP，再输入 TOTP 动态码；
4. 发送验证码；
5. 输入邮件验证码并确认。

验证成功后会撤销该用户的现有会话和刷新令牌，需要重新登录。

解绑邮箱同样要求当前密码，已启用 TOTP 时还要求动态码。解绑后，邮箱验证码登录和找回密码会失效，现有会话和刷新令牌会被撤销。

验证码登录入口是 `/account/login?mode=email`：

- 只对已绑定邮箱、启用、未锁定且通过安全策略的账号发送；
- 公共发送接口对不存在的账号也返回相同提示，不泄露账号是否存在；
- 登录步骤中的 TOTP 规则仍然生效。

找回密码入口是 `/account/forgot-password`：

- 输入已绑定邮箱；
- 发送 `reset-password` 验证码；
- 如果账号已启用 TOTP，还必须提供 TOTP 动态码；
- 新密码长度为 8 到 128；
- 成功后会撤销会话和刷新令牌，并让旧验证码失效。

### 5.4 账号中心、修改密码和登录记录

账号中心入口是 `/account`，当前包含：

- `/account/email`：绑定或更换邮箱；
- `/account/change-password`：修改密码；
- `/account/two-factor`：TOTP 管理；
- `/account/passkeys`：Passkey 管理；
- `/account/login-history`：最近 20 条登录记录。

修改密码要求：

- 当前密码正确；
- 新密码不能与当前密码相同；
- 新密码不能为空；
- 修改后撤销现有刷新令牌和 SSO session。

网页右上角的“退出所有设备”会撤销当前用户的所有刷新令牌和 SSO session，然后清除 Cookie。已经签发且由下游系统只做 JWT 本地验签的 access token，可能在其自身过期前仍然被接受；Host 的 introspection 和 userinfo 会因为 token epoch 或黑名单而拒绝这些 token。

### 5.5 TOTP

TOTP 页面是 `/account/two-factor`。

行为：

- 开始绑定需要当前密码；
- 默认注册有效期为 10 分钟；
- secret 使用 Data Protection 保护后保存；
- 动态码是 6 位数字，算法 SHA1，周期 30 秒；
- 验证时接受当前时间步及相邻各一个时间步；
- 已成功的计数器不会重复使用；
- 默认 `LoginFlow` 是 `conditional`，因此用户可以在账号中心关闭 TOTP；
- 如果登录流程把 TOTP 设为 `required`，账号中心不允许关闭 TOTP；
- 关闭 TOTP 后会撤销该用户的 SSO session 并退出登录。

如果 Data Protection key ring 丢失，已保存的 TOTP secret 无法解密，用户需要重新绑定。

### 5.6 Passkey / WebAuthn

默认 `WebAuthn:Enabled=false`。启用时，配置必须满足：

- `RelyingPartyId` 是不带 scheme 和路径的域名；
- `RelyingPartyName` 非空；
- `Origins` 是非空绝对 Origin 列表，每一项不带路径；
- `ChallengeLifetimeSeconds` 在 60 到 900 之间；
- 浏览器实际访问的域名和 Origin 必须与配置匹配。

Passkey 的注册和登录由 Host 调用 FIDO2 完成，服务端只保存公钥、凭据 ID、计数器等公开验证材料。注册时要求 resident key 和配置的 user verification，默认 `UserVerification=Required`。

入口：

- 登录页在启用 Passkey 后可以使用发现式凭据登录；
- `/account/passkeys` 可以添加、重命名、删除 Passkey；
- `/account/passkeys` 的添加和删除需要当前密码；已启用 TOTP 时还需要 TOTP；
- 删除 Passkey 后当前 SSO session 会失效；
- Passkey 管理服务的撤销操作还会撤销该用户刷新令牌和会话。

浏览器在非 localhost 环境应使用 HTTPS。RP ID 或 Origin 不匹配会导致浏览器拒绝 WebAuthn 操作，而不会进入 Host 的验证逻辑。

### 5.7 设备授权

设备授权流程由 `/connect/deviceauthorization` 和 `/device` 页面组成。

启动设备授权：

```bash
curl -sS -X POST 'http://localhost:5100/connect/deviceauthorization' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'client_id=your-device-client' \
  --data-urlencode 'client_secret=your-secret' \
  --data-urlencode 'scope=openid profile offline_access'
```

响应包含：

- `device_code`；
- `user_code`，格式为 8 个易读字符组成的 `XXXX-XXXX`；
- `verification_uri`，固定为 `/device` 的绝对地址；
- `verification_uri_complete`；
- `expires_in`；
- `interval`，默认 5 秒。

用户在浏览器打开 `verification_uri` 或 `verification_uri_complete`，登录后确认或拒绝请求。设备端按 `interval` 轮询 token 端点：

```bash
curl -sS -X POST 'http://localhost:5100/connect/token' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'grant_type=urn:ietf:params:oauth:grant-type:device_code' \
  --data-urlencode 'client_id=your-device-client' \
  --data-urlencode 'client_secret=your-secret' \
  --data-urlencode 'device_code=...'
```

设备授权行为：

- `device_code` 默认 15 分钟有效，配置项是 `Jwt:DeviceCodeLifetimeMinutes`；
- device_code 绑定启动它的客户端；
- 轮询过快返回 `slow_down`，并把后续 interval 增加 5 秒；
- 等待确认返回 `authorization_pending`；
- 过期返回 `expired_token`；
- 用户拒绝返回 `access_denied`；
- 成功后 device_code 只能消费一次；
- scope 必须在客户端允许范围内；资源 scope 必须对应启用的 API resource。

## 6. OAuth / OIDC 端点

### 6.1 发现端点

实现见 [`src/NexusAuth.Host/Controllers/OpenIdController.cs`](../src/NexusAuth.Host/Controllers/OpenIdController.cs)。

| 端点 | 方法 | 说明 |
| --- | --- | --- |
| `/.well-known/openid-configuration` | GET | OIDC Discovery |
| `/.well-known/jwks.json` | GET | 当前加载的验签公钥 |
| `/connect/authorize` | GET | 授权端点，只支持 `response_type=code` |
| `/connect/token` | POST | Token 端点 |
| `/connect/userinfo` | GET | OIDC UserInfo |
| `/connect/introspect` | POST | Token Introspection |
| `/connect/revocation` | POST | Token Revocation |
| `/connect/deviceauthorization` | POST | Device Authorization |
| `/connect/endsession` | GET / POST | OIDC RP-Initiated Logout |
| `/device` | GET / POST | 浏览器设备授权确认页 |

Discovery 对外声明的能力以实际实现为准：

- `response_types_supported = ["code"]`
- `response_modes_supported = ["query", "form_post"]`
- `grant_types_supported = ["authorization_code", "client_credentials", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code"]`
- `code_challenge_methods_supported = ["S256"]`
- `token_endpoint_auth_methods_supported = ["client_secret_post", "client_secret_basic", "client_secret_jwt", "private_key_jwt"]`
- `id_token_signing_alg_values_supported` 来自当前活跃签名密钥，默认 RSA 签名为 `RS256`
- `claims_parameter_supported = true`
- `request_parameter_supported = false`
- `request_uri_parameter_supported = false`

Discovery 中的 issuer 和所有绝对端点地址来自 `Jwt:Issuer`，不是从请求 Host 动态推断。生产环境必须把它配置成浏览器和客户端都能访问、且与 token `iss` 一致的 HTTPS 地址。

### 6.2 四种 grant

| `grant_type` | 用途 | 关键要求 |
| --- | --- | --- |
| `authorization_code` | 浏览器或服务端 Web 应用通过授权码换 token | 客户端必须允许该 grant；redirect URI 精确匹配；需要 PKCE 时必须提供 `S256`；授权码 10 分钟有效且只能消费一次 |
| `client_credentials` | 机器对机器访问资源 API | 客户端必须允许该 grant；`scope` 必填；不允许身份 scope；scope 必须对应启用的 API resource |
| `refresh_token` | 刷新用户 access token | 客户端必须允许该 grant；refresh token 必须属于该客户端；旧 refresh token 轮换后立即失效 |
| `urn:ietf:params:oauth:grant-type:device_code` | 设备授权码换 token | 客户端必须允许该 grant；必须先完成 `/connect/deviceauthorization` 和用户确认 |

授权码和设备码流程只有在请求 scope 包含 `offline_access` 时才签发 refresh token。scope 包含 `openid` 时才签发 ID token。

### 6.3 四种机密客户端认证

客户端认证实现见：

- [`OAuthClientAuthenticationParser.cs`](../src/NexusAuth.Application/Clients/OAuthClientAuthenticationParser.cs)
- [`ClientService.cs`](../src/NexusAuth.Application/Clients/ClientService.cs)
- [`ClientSecretJwtValidator.cs`](../src/NexusAuth.Application/Clients/ClientSecretJwtValidator.cs)
- [`ClientPrivateKeyJwtValidator.cs`](../src/NexusAuth.Application/Clients/ClientPrivateKeyJwtValidator.cs)

| 方法 | 传输方式 | 关键校验 |
| --- | --- | --- |
| `client_secret_basic` | `Authorization: Basic ...` | Basic 解码后的 client id/secret 必须正确；表单不能再同时提交其他认证材料 |
| `client_secret_post` | 表单 `client_id` + `client_secret` | 必须与该客户端配置的认证方法一致 |
| `client_secret_jwt` | 表单 `client_assertion_type` + `client_assertion` | JWT 算法必须是 `HS256`；`iss` 和 `sub` 都必须是 client id；`aud` 必须是目标端点 URL；`exp` 必须存在；`jti` 必须存在且防重放 |
| `private_key_jwt` | 表单 `client_assertion_type` + `client_assertion` | JWT 算法必须是 `RS256`；客户端必须注册 JWK Set；`iss` 和 `sub` 都必须是 client id；`aud` 必须是目标端点 URL；`exp` 必须存在；`jti` 必须存在且防重放 |

客户端断言类型必须是：

```text
urn:ietf:params:oauth:client-assertion-type:jwt-bearer
```

客户端断言的可接受 audience 是 Host 实际收到请求时的端点绝对地址，例如 `https://sso.example.com/connect/token`。反向代理必须正确传递 scheme、host 和 path base，否则 JWT 的 `aud` 会与客户端预期不一致。

实际支持客户端认证的端点：

| 端点 | 是否解析四种客户端认证 |
| --- | --- |
| `/connect/token` | 是 |
| `/connect/introspect` | 是 |
| `/connect/revocation` | 是 |
| `/connect/deviceauthorization` | 是 |
| `/connect/authorize` | 不在该端点做客户端 secret/assertion 认证，授权码换取 token 时再认证 |
| `/connect/endsession` | 不做客户端 secret/assertion 认证；使用 `id_token_hint` 和已登记的 post logout redirect URI 校验 |
| `/connect/userinfo` | 使用 Bearer access token |

每个客户端在登记时只配置一种 `token_endpoint_auth_method`。不能用同一个客户端同时准备两套认证材料来绕过该约束。

已知约束：

- `private_key_jwt` 校验器读取客户端已登记的 `Jwks` 字段；当前不会主动下载 `JwksUri`。只配置 `JwksUri` 会导致认证失败。
- `client_secret_jwt` 需要可读取的 shared secret 明文材料。把原本用于 `client_secret_basic` 或 `client_secret_post` 的客户端直接改成 `client_secret_jwt`，可能因为旧 secret 未保存可读取材料而失败；应重新生成对应类型的 secret。

### 6.4 授权码流程

典型请求：

```text
GET /connect/authorize
  ?response_type=code
  &client_id=web-client
  &redirect_uri=https%3A%2F%2Fapp.example.com%2Fsignin-oidc
  &scope=openid%20profile%20offline_access
  &state=...
  &nonce=...
  &code_challenge=...
  &code_challenge_method=S256
```

行为：

- `client_id`、`redirect_uri`、`response_type`、`scope` 必填；
- `response_type` 只允许 `code`；
- redirect URI 必须在客户端登记列表中精确匹配；
- 客户端要求 PKCE 时，必须有 `code_challenge`，且 `code_challenge_method=S256`；
- `response_mode` 只允许 `query` 或 `form_post`；
- `prompt` 支持 `none`、`login`、`consent`；`none` 不能与其他值组合；
- `max_age` 用于检查登录时间；
- `claims` 必须是合法 JSON；
- 未登录时跳转 `/account/login`，已登录时直接签发授权码；
- 授权码 10 分钟有效，只能使用一次；
- 换 token 时必须在 `/connect/token` 再次完成客户端认证。

### 6.5 Client Credentials

示例：

```bash
curl -sS -X POST 'http://localhost:5100/connect/token' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'grant_type=client_credentials' \
  --data-urlencode 'client_id=service-client' \
  --data-urlencode 'client_secret=...' \
  --data-urlencode 'scope=permission-center-api'
```

约束：

- 不接受身份 scope：`openid`、`profile`、`email`、`phone`、`address`、`offline_access`；
- 请求 scope 必须属于该客户端，且对应启用的 API resource；
- 签发的 access token 是客户端身份，不是用户身份；
- 该 token 不能调用 `/connect/userinfo`；
- 多个资源 audience 不能出现在同一个 token 请求中。

### 6.6 Refresh Token

授权码或设备码流程只有在 scope 包含 `offline_access` 时才返回 refresh token。

刷新请求：

```bash
curl -sS -X POST 'http://localhost:5100/connect/token' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'grant_type=refresh_token' \
  --data-urlencode 'client_id=web-client' \
  --data-urlencode 'client_secret=...' \
  --data-urlencode 'refresh_token=...'
```

行为：

- refresh token 必须属于当前认证的客户端；
- 每次刷新都会轮换 refresh token；
- 旧 refresh token 立即失效；
- 如果用户不活跃、token 已撤销或 token 在用户 `TokenInvalidBefore` 之前，刷新失败；
- 默认 refresh token 有效期是 `Jwt:RefreshTokenLifetimeMinutes`，默认 43200 分钟，也就是 30 天。

### 6.7 UserInfo

`/connect/userinfo` 要求 Bearer access token：

```bash
curl -sS 'http://localhost:5100/connect/userinfo' \
  -H 'Authorization: Bearer <access_token>'
```

行为：

- 只接受 `token_use=access_token` 的用户 access token；
- 客户端凭据 token 没有用户 subject，不能调用；
- 返回的 claim 受 scope 和授权时 `claims` 参数影响；
- `email` 和 `phone` 相关 claim 需要请求对应 scope；
- 当前 `email_verified` 和 `phone_number_verified` 不表示已经完成 Host 的邮箱所有权验证，实现中这两个 claim 会返回 `false`。

### 6.8 Token Introspection 和 Revocation

Introspection：

```bash
curl -sS -X POST 'http://localhost:5100/connect/introspect' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'client_id=web-client' \
  --data-urlencode 'client_secret=...' \
  --data-urlencode 'token=<access_or_id_token>'
```

Introspection 返回：

- `active`
- `sub`
- `client_id`
- `scope`
- `exp`
- `iss`
- `token_use`

传了客户端 ID 时，token 的 `client_id` 必须与该客户端一致；否则返回 `active=false`。

Revocation：

```bash
curl -sS -X POST 'http://localhost:5100/connect/revocation' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'client_id=web-client' \
  --data-urlencode 'client_secret=...' \
  --data-urlencode 'token=<token>' \
  --data-urlencode 'token_type_hint=refresh_token'
```

行为：

- `token_type_hint=refresh_token` 时只尝试撤销 refresh token；
- 其他情况下先尝试撤销 access token，再尝试撤销 refresh token；
- token 所属客户端不匹配时不会撤销；
- 无论 token 是否存在，端点通常都返回成功，避免泄露 token 状态；
- access token 撤销后写入黑名单，Host 后续 introspection、userinfo 和 Workbench Bearer 校验会拒绝它。

用户级撤销会同时做到：

- 撤销全部 refresh token；
- 更新用户的 `TokenInvalidBefore` / token epoch；
- 撤销全部 SSO session；
- 让 Host 侧后续 introspection 和 userinfo 拒绝旧 access token。

OIDC 注销端点 `/connect/endsession` 先校验可选的 `id_token_hint` 和已登记的 `post_logout_redirect_uri`，再显示本地确认页。POST 请求需要防伪令牌；确认后，如果当前 Cookie 中存在有效用户和 SSO session，Host 会撤销该用户全部 refresh token 和 SSO session，再清除 Cookie。仅 GET 展示确认页本身不会注销。

如果下游资源服务只做 JWT 本地验签，并且不调用 introspection，也不检查 token epoch，那么它可能在 access token 自身过期前继续接受旧 token。需要立即失效时，资源服务必须接入 introspection 或增加相应的撤销检查。

## 7. SCIM 2.0 用户供给

实现见 [`src/NexusAuth.Host/Controllers/ScimController.cs`](../src/NexusAuth.Host/Controllers/ScimController.cs) 和 [`src/NexusAuth.Host/Filters/ScimAuthorizeAttribute.cs`](../src/NexusAuth.Host/Filters/ScimAuthorizeAttribute.cs)。

基础地址：

```text
http://localhost:5100/scim/v2
```

请求和响应使用：

```text
Content-Type: application/scim+json
```

认证方式：

```http
Authorization: Bearer <scim-token>
```

凭证必须是 `scim_service_principal_credentials` 中登记的有效 token。原始 token 只在创建时返回一次，数据库只保存 SHA-256 的 unpadded Base64Url 哈希。

### 7.1 路由和 scope

| 端点 | 方法 | 需要 scope |
| --- | --- | --- |
| `/scim/v2/ServiceProviderConfig` | GET | `scim:read` |
| `/scim/v2/ResourceTypes` | GET | `scim:read` |
| `/scim/v2/Schemas` | GET | `scim:read` |
| `/scim/v2/Schemas/{id}` | GET | `scim:read` |
| `/scim/v2/Users` | GET | `scim:read` |
| `/scim/v2/Users/{id}` | GET | `scim:read` |
| `/scim/v2/Users` | POST | `scim:write` |
| `/scim/v2/Users/{id}` | PUT | `scim:write` |
| `/scim/v2/Users/{id}` | PATCH | `scim:write` |
| `/scim/v2/Users/{id}` | DELETE | `scim:write` |

认证结果：

- 没有 Bearer token 或 token 无效：GET 返回 `401`，SCIM 错误类型是 `invalidToken`；
- token 只有 `scim:read`，调用写接口：返回 `403`，SCIM 错误类型是 `forbidden`；
- 写权限校验发生在请求体绑定之前。

### 7.2 支持的 SCIM User 字段

主要字段：

- `userName`
- `externalId`
- `active`
- `name.givenName`
- `name.familyName`
- `name.middleName`
- `name.honorificPrefix`
- `name.honorificSuffix`
- `emails`
- `phoneNumbers`
- `profileUrl`
- `title`
- `userType`
- `preferredLanguage`
- `locale`
- `timezone`

当前只提供 User resource，不提供 Group resource。`emails` 和 `phoneNumbers` 目前只支持一个有效值；提交多个值会返回 `400 invalidValue`。

### 7.3 查询、过滤和分页

`GET /scim/v2/Users` 支持：

- `startIndex`，最小为 1；
- `count`，最大 200；
- `filter`

当前支持的相等过滤条件只有：

```text
userName eq "alice"
externalId eq "idp-1001"
active eq true
emails.value eq "alice@example.com"
```

其他 filter 会返回 `400 invalidFilter`。

### 7.4 ETag 和并发

User 响应会返回 `ETag`。`PUT`、`PATCH`、`DELETE` 可以带 `If-Match`：

- `If-Match` 为空或 `*` 时，当前实现不做版本比较；
- 提供了具体 ETag 且不匹配时，返回 `412`；
- 用户不存在时，更新和删除同样返回 `412`。

### 7.5 调用示例

只读目录：

```bash
curl -sS 'http://localhost:5100/scim/v2/Users?filter=userName%20eq%20%22alice%22' \
  -H 'Accept: application/scim+json' \
  -H 'Authorization: Bearer <scim:read-token>'
```

创建用户：

```bash
curl -sS -X POST 'http://localhost:5100/scim/v2/Users' \
  -H 'Authorization: Bearer <scim:read-write-token>' \
  -H 'Content-Type: application/scim+json' \
  --data-binary '{
    "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
    "userName": "alice",
    "externalId": "idp-1001",
    "active": true,
    "name": {
      "givenName": "Alice",
      "familyName": "Zhang"
    },
    "emails": [
      {
        "value": "alice@example.com",
        "primary": true
      }
    ]
  }'
```

PATCH 停用用户：

```bash
curl -sS -X PATCH 'http://localhost:5100/scim/v2/Users/<user-id>' \
  -H 'Authorization: Bearer <scim:read-write-token>' \
  -H 'Content-Type: application/scim+json' \
  -H 'If-Match: "<etag>"' \
  --data-binary '{
    "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
    "Operations": [
      {
        "op": "replace",
        "path": "active",
        "value": false
      }
    ]
  }'
```

### 7.6 SCIM 凭证从哪来

Host 没有 SCIM 凭证管理端点。实际凭证通过 Workbench 管理界面或共享的应用/持久化代码创建。`ScimCredentialService` 默认创建的 scopes 是：

```text
scim:read scim:write
```

调用方应按最小权限创建只读凭证。原始 token 只在创建时返回一次。

根 Compose 的全新数据库卷还会由 [`docker/postgres-init.sh`](../docker/postgres-init.sh) 写入两个本地集成测试凭证的哈希。它们只用于本地 Compose 和集成测试，原始 token 位于测试代码中，不能用于生产。不要把本地测试凭证当作正式部署的凭证管理方式。

## 8. 只读 Open API

实现见 [`src/NexusAuth.Host/Controllers/OpenApiController.cs`](../src/NexusAuth.Host/Controllers/OpenApiController.cs) 和 [`src/NexusAuth.Host/Filters/OpenApiAuthorizeAttribute.cs`](../src/NexusAuth.Host/Filters/OpenApiAuthorizeAttribute.cs)。

基础地址：

```text
http://localhost:5100/openapi/v1
```

认证方式：

```http
Authorization: Bearer <open-api-token>
```

Open API 凭证有两种目标类型，不能交叉使用：

| 目标类型 | scope | 可调用端点 |
| --- | --- | --- |
| `application` | `application:read` | `/openapi/v1/applications` |
| `service_resource` | `service_resource:read` | `/openapi/v1/service-resources` |

凭证校验由 [`OpenApiCredentialService.cs`](../src/NexusAuth.Application/Services/OpenApi/OpenApiCredentialService.cs) 完成。`OpenApiCredential` 的实体定义见 [`src/NexusAuth.Domain/Entities/OpenApiCredential.cs`](../src/NexusAuth.Domain/Entities/OpenApiCredential.cs)。

读取应用目录：

```bash
curl -sS 'http://localhost:5100/openapi/v1/applications?keyword=permission' \
  -H 'Authorization: Bearer <application:read-token>'
```

读取服务资源目录：

```bash
curl -sS 'http://localhost:5100/openapi/v1/service-resources' \
  -H 'Authorization: Bearer <service_resource:read-token>'
```

返回内容：

- applications：`id`、`clientId`、`clientName`、`description`、`isActive`、`createdAt`；
- service-resources：`id`、`name`、`displayName`、`audience`、`description`、`isActive`、`createdAt`。

行为：

- 只返回启用状态的对象；
- 不返回客户端 secret、redirect URI、allowed scope 等管理敏感字段；
- token 无效、过期、撤销、目标类型不匹配时返回 `401 invalid_token`；
- applications 的 `keyword` 匹配 `clientId` 或 `clientName`；
- service-resources 的 `keyword` 匹配 `name`、`displayName` 或 `audience`。

Open API 凭证没有 Host 管理端点。通过 Workbench 或共享应用服务创建，原始 token 只在创建时显示一次。每个凭证只获得其目标类型对应的一个只读 scope。

## 9. 限流和安全策略

Host 在 [`src/NexusAuth.Host/AppWebModule.cs`](../src/NexusAuth.Host/AppWebModule.cs) 中配置了按客户端地址和端点族分区的 token bucket 限流。超限返回 `429`，并带 `Retry-After: 60`。

主要限制：

| 端点族 | 每分钟 token 上限 |
| --- | --- |
| `/account/forgot-password`、`/account/email` | 10 |
| `/account/login` | 10 |
| `/account/passkey`、`/account/passkeys`、`/account/PasskeyEnrollment` | 10 |
| `/account/register` | 5 |
| `/connect/token` | 60 |
| `/connect/deviceauthorization` | 20 |
| `/connect/introspect` | 120 |
| `/connect/revocation` | 60 |
| `/device` | 30 |

限流是按实例内存执行的。多实例部署需要反向代理层或分布式限流来获得全局限流效果。

安全策略配置：

| 配置键 | 作用 |
| --- | --- |
| `Security:ClientWhitelist` | 非空时只允许列表中的 client id |
| `Security:ClientBlacklist` | 拒绝列表中的 client id |
| `Security:UserWhitelist` | 非空时只允许列表中用户名或用户 ID |
| `Security:UserBlacklist` | 拒绝列表中用户名或用户 ID |
| `Security:LoginFailureLimit` | 密码失败次数上限，默认 5 |
| `Security:LoginLockoutMinutes` | 锁定分钟数，默认 5 |

## 10. 排错

### 10.1 Host 启动失败

先看进程输出和 `logs/nexusauth-sso-*.log`，再检查以下常见原因：

1. `Smtp:Enabled=true` 但没有 `Smtp:Password`。
   - 无邮件环境：设置 `NEXUSAUTH_SMTP_ENABLED=false`。
   - 有邮件环境：补全 SMTP 密码和发件人。

2. `BootstrapAdmin:Username` 或 `BootstrapAdmin:Password` 为空。
   - 两项必须同时配置。已有用户不会用新配置密码覆盖。

3. 生产环境找不到签名证书或密钥。
   - 检查 `Jwt:SigningPath`、`Jwt:SigningCertificatePath` 或 `Jwt:SigningKeys[*].Path`。
   - 开发环境可以自动生成活跃签名材料；生产环境不会。

4. 数据库连接失败。
   - 检查 `ConnectionStrings:Default`、数据库主机、端口、用户、密码和 `Search Path=nexusauth`。

5. 数据库表不存在。
   - 检查是否对实际数据库执行了 `production-init.sql`，不要期待应用自动迁移。

### 10.2 登录或 Cookie 异常

- 确认 `Jwt:Issuer` 与浏览器访问的公开地址一致。
- 确认 Data Protection key ring 没有被清空或更换。
- 检查反向代理是否保留了 `Host`、`X-Forwarded-Proto`；如启用转发头处理，确认可信代理配置正确。
- 检查用户是否被安全策略列入黑名单或不在白名单中。
- 检查账号是否因登录失败被锁定。

### 10.3 邮箱验证码问题

- 确认 SMTP 已启用且密码、Host、Port、Security、Username、FromAddress 全部有效。
- 检查是否处于 60 秒冷却期。
- 检查同一用途、同一账号、同一密码版本是否已有未过期验证码。
- 检查是否达到每小时 10 封的发送上限。
- 检查验证码是否已输入错误 5 次；错误次数达到上限后验证码会失效。
- 确认 Host 重启后旧验证码是否已丢失，或是否存在多个 Host 实例。

### 10.4 OAuth / OIDC 问题

- `invalid_client`：客户端认证方法或 secret/assertion 不正确。
- JWT client assertion 失败：检查 `alg`、`iss`、`sub`、`aud`、`exp`、`jti`。
- `invalid_grant`：授权码、refresh token、device_code 或用户状态不符合要求。
- `invalid_scope`：scope 不在客户端允许列表中，或资源 scope 没有对应的启用 API resource。
- `unauthorized_client`：客户端没有允许对应的 grant type。
- `login_required`：`prompt=none` 或 `max_age` 要求重新登录。
- `invalid_token`：access token 无效、已过期、已撤销、不是 access token 或不属于当前客户端。

### 10.5 Passkey 问题

- 浏览器必须使用与 `WebAuthn:RelyingPartyId` 和 `WebAuthn:Origins` 匹配的地址。
- 非 localhost 环境应使用 HTTPS。
- 如果用户删除了最后一个 Passkey，当前登录会失效。
- 如果 Data Protection key ring 改变，旧的受保护流程状态会失效。

## 11. 生产检查清单

1. 使用 `ASPNETCORE_ENVIRONMENT=Production`，不要把开发默认值带到生产。
2. 为 `Jwt:Issuer` 配置公开 HTTPS 地址，并确保 token 的 `iss`、Discovery 和客户端校验一致。
3. 预置受管 RSA 签名证书或密钥文件；不要把私钥提交到仓库。
4. 需要轮换时使用 `Jwt:SigningKeys` 数组，先并行发布新旧公钥，再移除旧密钥。
5. 持久化并备份 Data Protection key ring；多副本共享同一 key ring。
6. 配置真实的 SMTP；不要把授权码写入镜像、仓库或普通配置文件。
7. 覆盖 `BootstrapAdmin` 的用户名和密码；已有管理员密码不会被配置自动重置。
8. 使用独立数据库账号、强密码和受控网络，并预先执行当前 schema；Host 不代替迁移工具。
9. 在反向代理后只信任明确的代理 IP 或 CIDR，代理入口覆盖客户端转发头。
10. 使用 HTTPS、受控日志目录、日志轮转和集中日志采集。
11. 为 SCIM 和 Open API 分别使用最小权限凭证，定期轮换和撤销。
12. 多实例部署前，确认邮箱验证码、滑块验证码状态和限流策略是否需要迁移到共享存储或网关层。

## 12. 源码核对索引

本手册主要直接核对以下源码：

- Host 启动和配置：[`src/NexusAuth.Host/Program.cs`](../src/NexusAuth.Host/Program.cs)
- Host 服务注册、认证、限流：[`src/NexusAuth.Host/AppWebModule.cs`](../src/NexusAuth.Host/AppWebModule.cs)
- Host 默认配置：[`src/NexusAuth.Host/appsettings.json`](../src/NexusAuth.Host/appsettings.json)
- Host 开发配置：[`src/NexusAuth.Host/appsettings.Development.json`](../src/NexusAuth.Host/appsettings.Development.json)
- 启动 profile：[`src/NexusAuth.Host/Properties/launchSettings.json`](../src/NexusAuth.Host/Properties/launchSettings.json)
- Compose：[`docker-compose.yml`](../docker-compose.yml)
- 数据库初始化：[`docker/postgres-init.sh`](../docker/postgres-init.sh)、[`production-init.sql`](../production-init.sql)
- 管理员初始化：[`src/NexusAuth.Host/BootstrapAdminHostedService.cs`](../src/NexusAuth.Host/BootstrapAdminHostedService.cs)
- 邮箱实现：[`src/NexusAuth.Application/Services/Email/EmailAccountService.cs`](../src/NexusAuth.Application/Services/Email/EmailAccountService.cs)、[`EmailVerificationCodeService.cs`](../src/NexusAuth.Application/Services/Email/EmailVerificationCodeService.cs)
- SMTP 选项和校验：[`src/NexusAuth.Shared/Email/SmtpOptions.cs`](../src/NexusAuth.Shared/Email/SmtpOptions.cs)
- Token 端点：[`src/NexusAuth.Host/Controllers/TokenController.cs`](../src/NexusAuth.Host/Controllers/TokenController.cs)
- OIDC 端点：[`src/NexusAuth.Host/Controllers/OpenIdController.cs`](../src/NexusAuth.Host/Controllers/OpenIdController.cs)
- 授权端点：[`src/NexusAuth.Host/Controllers/AuthorizeController.cs`](../src/NexusAuth.Host/Controllers/AuthorizeController.cs)
- 客户端认证：[`src/NexusAuth.Application/Clients/OAuthClientAuthenticationParser.cs`](../src/NexusAuth.Application/Clients/OAuthClientAuthenticationParser.cs)
- Token 业务：[`src/NexusAuth.Application/Services/Tokens/TokenService.cs`](../src/NexusAuth.Application/Services/Tokens/TokenService.cs)
- 签名配置：[`src/NexusAuth.Application/Services/Tokens/JwtOptions.cs`](../src/NexusAuth.Application/Services/Tokens/JwtOptions.cs)、[`TokenSigningCredentialsProvider.cs`](../src/NexusAuth.Application/Services/Tokens/TokenSigningCredentialsProvider.cs)
- 用户和 TOTP：[`src/NexusAuth.Application/Users/UserService.cs`](../src/NexusAuth.Application/Users/UserService.cs)、[`TotpService.cs`](../src/NexusAuth.Application/Users/TotpService.cs)
- Passkey：[`src/NexusAuth.Application/Services/WebAuthn/WebAuthnService.cs`](../src/NexusAuth.Application/Services/WebAuthn/WebAuthnService.cs)、[`PasskeyManagementService.cs`](../src/NexusAuth.Application/Services/WebAuthn/PasskeyManagementService.cs)
- SCIM：[`src/NexusAuth.Host/Controllers/ScimController.cs`](../src/NexusAuth.Host/Controllers/ScimController.cs)、[`ScimCredentialService.cs`](../src/NexusAuth.Application/Services/Scim/ScimCredentialService.cs)
- Open API：[`src/NexusAuth.Host/Controllers/OpenApiController.cs`](../src/NexusAuth.Host/Controllers/OpenApiController.cs)、[`OpenApiCredentialService.cs`](../src/NexusAuth.Application/Services/OpenApi/OpenApiCredentialService.cs)
- 反向代理：[`src/NexusAuth.Host/Authentication/ClientIpConfiguration.cs`](../src/NexusAuth.Host/Authentication/ClientIpConfiguration.cs)

## 13. 已知限制

- Host 不提供客户端、API 资源、SCIM 凭证或 Open API 凭证管理端点。
- Host 不执行数据库自动迁移。
- 默认 `Smtp:Enabled=true` 且没有密码，不调整配置时无法直接启动。
- 根 Compose 只向 `sso` 容器转发 SMTP 密码，其他 SMTP 变量不会从宿主 `.env` 自动进入容器。
- `private_key_jwt` 当前只读取已登记的 JWK Set，不远程下载 `JwksUri`。
- OIDC 只支持授权码流程，不支持隐式、混合、`request` 或 `request_uri`。
- SCIM 只实现 User resource，过滤字段有限，不提供 Group。
- 邮箱验证码和限流状态是进程内存状态，多实例部署需要额外的共享存储或网关能力。
- 只做本地验签的资源服务可能无法立即感知撤销；需要 introspection 或 token epoch 检查。
- Workbench 和 Host 共用数据库和部分 Application/Persistence 代码，但仍是两套应用，部署和排错不能混在一起。
