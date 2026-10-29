# NexusAuth.Host 使用手册

本文依据当前 Host 的 `appsettings.json`、`appsettings.Development.json` 和实际配置读取、校验代码整理。Host 提供 OAuth 2.1 / OpenID Connect、登录页、账号安全中心、SCIM 用户供给及只读 Open API，可以独立运行。

## 1. 先确认哪些配置必须填写

“必须”分为运行所需和启用特定功能后所需。多数配置已有默认值，不需要照着完整配置示例逐项填写。

| 配置或准备项 | 是否必须 | 说明 |
| --- | --- | --- |
| PostgreSQL 数据库及表结构 | 必须 | 先执行仓库的 `production-init.sql` 初始化新库；Host 不自动建表或迁移 |
| `ConnectionStrings:Default` | 必须核对并配置 | 必须能连接实际数据库，包含正确账号、密码及 `Search Path=nexusauth`；不要依赖代码中的本机回退连接串 |
| `Jwt:Issuer` | 必须核对并配置 | 对外访问的认证服务地址；本地默认 `http://localhost:5100`，生产使用实际 HTTPS 地址，并与客户端 Authority 一致 |
| `Jwt:DefaultAudience` | 必须保留有效值 | 默认 `NexusAuth`，未从请求或 API 资源解析到 audience 时使用；可以沿用默认值 |
| JWT 签名材料 | 必须 | Development 可以生成开发文件；Production 等非 Development 环境必须预先提供文件并配置路径 |
| `Jwt:SigningPassword` | 使用加密 PFX/P12 时必须 | 应与实际证书密码一致；无密码证书或 `RsaKeyFile` 不要求此项 |
| `BootstrapAdmin:Username`、`Password` | 初始化管理员时必须成对填写 | 两项都为空则跳过；只有一项会导致启动失败；服务运行不强制要求初始化管理员 |
| `Smtp` | 启用邮件功能后必须补齐 | 默认关闭；邮箱验证码登录、邮箱绑定/更换、找回密码需要可用 SMTP |
| `WebAuthn` | 启用 Passkey 后必须核对 | RP ID、名称、Origins 必须有效，并与浏览器实际访问地址匹配 |
| `WeChat:MiniProgram` | 启用微信小程序登录后必须补齐 | 必须有 `ClientId`、`AppId`、`AppSecret` 和合法上游地址；还要登记对应 OAuth 客户端 |
| `ClientIp` | 启用转发头后必须补齐 | `KnownProxies` 或 `KnownNetworks` 至少一个非空 |
| 其余功能与展示配置 | 可选 | 可保留默认值；部分数值即使对应功能关闭，也仍会在启动时校验，详见后文 |

> `Jwt:Issuer` 和数据库连接在当前代码中没有完整的启动必填校验；缺少启动报错不代表配置正确。这里的“必须”也包含服务正常使用所需的配置。

完整配置键参考 [`document/appsettings.Host.complete.example.json`](../../document/appsettings.Host.complete.example.json)。它用于查阅，不是可直接部署的生产配置，其中地址和凭证需要替换。

## 2. 配置从哪里读取

Host 的业务配置按以下顺序合并，后者覆盖前者的同名键：

1. Host 内容根目录下的 [`appsettings.json`](appsettings.json)。
2. 同目录下的 `appsettings.{环境名}.json`，例如 [`appsettings.Development.json`](appsettings.Development.json) 或自行创建的 `appsettings.Production.json`。

[`Program.cs`](Program.cs) 会清空默认配置源，再加入上述两个 JSON 文件。因此：

- `ConnectionStrings__Default`、`Jwt__Issuer`、`Smtp__Password` 等环境变量不会覆盖 Host 的业务配置；`NEXUSAUTH_*` 同样不是 Host 业务配置源。
- 命令行中的 `--Jwt:Issuer=...` 等参数以及 User Secrets 也不会作为业务配置源保留。
- `ASPNETCORE_ENVIRONMENT` 仍用于选择运行环境；`ASPNETCORE_URLS` 或 `--urls` 指定的监听地址会在清空配置源前保留。
- 这两个 JSON 文件均按可选文件加载，但业务仍然需要有效配置。不存在的环境文件不会自动创建。
- 配置按键合并，环境文件无需重复整份基础配置。数组也按索引合并：缩短 `LoginFlow:Steps` 或 `Jwt:SigningKeys` 时，要同时核对基础文件，避免遗留条目。
- 当前加载方式没有启用 JSON 热更新。修改后需要重启 Host；修改启动环境或挂载路径时需要重建容器。

`Jwt:Issuer` 是协议使用的对外地址，监听地址是进程绑定的地址，两者分别配置。例如容器内部监听 `8080`，对外 Issuer 可以是 `https://sso.example.com`。

### 当前源码和 Docker 默认值的区别

| 项目 | 源码基础配置 | Development 覆盖 | 根 Compose 的 `sso` |
| --- | --- | --- | --- |
| 数据库地址 | `localhost:5432` | `127.0.0.1:33210` | `db:5432` |
| Issuer | `http://localhost:5100` | 沿用基础配置 | `http://localhost:5100` |
| 签名模式 | `Certificate` | `Certificate`，配置开发签名密码 | `Certificate`，配置 Compose 开发签名密码 |
| 自注册 | 关闭 | 关闭 | 开启 |
| Passkey | 关闭 | 关闭 | 开启 |
| WebAuthn Origins | `http://localhost:5200` | 沿用基础配置 | `http://localhost:5100` |
| SMTP | 关闭 | 关闭 | 关闭 |
| SCIM | 开启 | 开启 | 开启 |
| Swagger | 未显式配置 | 默认开启 | 默认 Development，开启 |
| 初始管理员 | 不创建 | 不创建 | 不创建 |

源码 launch profile 只设置 Development 环境和 `http://localhost:5100` 监听地址，不额外开启自注册或 Passkey。**源码运行时如启用 Passkey，必须把当前 `5200` 的 Origin 改为实际访问的 `5100` 或其他地址。**

## 3. 本地启动

环境要求：.NET SDK 10（目标框架 `net10.0`）及 PostgreSQL；仓库 Compose 使用 PostgreSQL 16。以下命令均从仓库根目录执行。

### 3.1 从源码运行

先启动数据库：

```bash
docker compose up -d db
```

全新数据卷首次启动时，[`docker/postgres-init.sh`](../../docker/postgres-init.sh) 会创建 `nexusauth` 数据库并执行 [`production-init.sql`](../../production-init.sql)。等待数据库初始化完成后，核对 `appsettings.Development.json` 的连接串；当前默认值与根 Compose 的数据库开发账号一致。

最小开发覆盖配置示例：将所需字段合并到 `appsettings.Development.json`，并替换实际连接凭证。

```json
{
  "ConnectionStrings": {
    "Default": "Host=127.0.0.1;Port=33210;Database=nexusauth;Username=nexusauth;Password=REPLACE_WITH_DATABASE_PASSWORD;Search Path=nexusauth"
  },
  "Jwt": {
    "Issuer": "http://localhost:5100",
    "DefaultAudience": "NexusAuth",
    "SigningMode": "Certificate",
    "SigningPassword": "REPLACE_WITH_LOCAL_SIGNING_PASSWORD"
  }
}
```

启动：

```bash
dotnet run --project src/NexusAuth.Host/NexusAuth.Host.csproj --launch-profile NexusAuth.Host
```

也可以不使用 launch profile，显式指定环境和监听地址：

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5100 \
dotnet run --project src/NexusAuth.Host/NexusAuth.Host.csproj --no-launch-profile
```

Development 单密钥模式在文件缺失时会生成 `App_Data/development-signing-certificate.pfx`。已存在的证书不会因为更换配置密码而重新生成，密码必须与文件匹配。

如需首次登录，可在环境配置中成对设置 `BootstrapAdmin:Username`、`Password` 创建账号，或显式开启 `SelfRegistration:Enabled` 后注册。空数据库、未配置管理员且未开启自注册时，不会自动产生可登录账号。

### 3.2 只运行容器版 Host 和数据库

```bash
docker compose up -d --build db sso
docker compose logs -f sso
```

根 [`docker-compose.yml`](../../docker-compose.yml) 将 [`docker/host.appsettings.json`](../../docker/host.appsettings.json) 只读挂载到 `/app/appsettings.Development.json`，默认外部地址为 `http://localhost:5100`，容器端口为 `8080`。此配置会开启自注册与 Passkey。

自定义部署文件时，先准备好 JSON，再指定文件路径：

```bash
NEXUSAUTH_HOST_APPSETTINGS_FILE=./docker/my-host.appsettings.json \
docker compose up -d --build db sso
```

`NEXUSAUTH_HOST_APPSETTINGS_FILE` 和 `NEXUSAUTH_SSO_ENVIRONMENT` 由 **Compose** 解析，用来选择挂载文件及环境；它们不会直接给 Host 注入 JWT、数据库或 SMTP 参数。若修改了 `POSTGRES_USER` / `POSTGRES_PASSWORD`，要同步修改 JSON 中的连接串；已有数据库卷中的账号密码不会随 Compose 变量自动改变。

仅修改已挂载 JSON 的内容后，可执行 `docker compose restart sso`；修改挂载路径或 Compose 环境变量后，用同一组变量重新执行 `docker compose up -d sso`。

### 3.3 使用已有或外部数据库

连接串中的 `Host`、`Port`、`Database`、`Username`、`Password` 填实际值，使用仓库表结构时保留 `Search Path=nexusauth`。容器连接数据库应使用容器服务名或实际数据库地址，不能把容器内 `localhost` 当作宿主机数据库。

新库可在创建数据库后执行：

```bash
psql --host=127.0.0.1 --port=5432 --username=YOUR_DATABASE_USER \
  --dbname=nexusauth --set=ON_ERROR_STOP=1 --file=production-init.sql
```

`production-init.sql` 是新库的最终建表脚本，含 `CREATE TABLE`，不是已有库的通用升级脚本。Compose 初始化钩子只对全新数据卷执行；已有库缺表时需要准备对应迁移。Host 不会执行 `Migrate` 或 `EnsureCreated`。

## 4. JWT 与生产环境必需配置

### 4.1 基础配置

| 键 | 当前默认值 | 用途 |
| --- | --- | --- |
| `Jwt:Issuer` | `http://localhost:5100`（基础 JSON） | Discovery 地址和令牌 `iss`；生产改为实际对外 HTTPS 地址，建议不带结尾 `/` |
| `Jwt:DefaultAudience` | `NexusAuth`（基础 JSON） | access token 的默认 audience；实际还受请求和 API 资源配置影响 |
| `Jwt:SigningMode` | `Certificate` | 单密钥源：`Certificate` 或 `RsaKeyFile` |
| `Jwt:SigningPath` | 空 | 单密钥文件路径；相对路径基于 Host 内容根目录 |
| `Jwt:SigningPassword` | 基础 JSON 未设置 | PFX/P12 密码；Development 文件另有开发覆盖值 |
| `Jwt:AccessTokenLifetimeMinutes` | `60` | access token 和 ID token 有效期，单位分钟 |
| `Jwt:DeviceCodeLifetimeMinutes` | `15` | 设备授权码有效期，单位分钟 |
| `Jwt:RefreshTokenLifetimeMinutes` | `43200` | refresh token 有效期，单位分钟，即 30 天 |

这些生命周期应填写正数；当前 `JwtOptions` 没有统一的范围启动校验。

### 4.2 生产配置示例

创建 `appsettings.Production.json`，将以下占位符替换为实际值，并准备可读取的签名文件：

```json
{
  "AllowedHosts": "sso.example.com",
  "ConnectionStrings": {
    "Default": "Host=db.example.com;Port=5432;Database=nexusauth;Username=nexusauth;Password=REPLACE_WITH_DATABASE_PASSWORD;Search Path=nexusauth"
  },
  "Jwt": {
    "Issuer": "https://sso.example.com",
    "DefaultAudience": "NexusAuth",
    "SigningMode": "Certificate",
    "SigningPath": "/run/secrets/nexusauth-signing.pfx",
    "SigningPassword": "REPLACE_WITH_SIGNING_PASSWORD"
  },
  "Security": {
    "CookieSecurePolicy": "Always"
  },
  "SelfRegistration": { "Enabled": false },
  "WebAuthn": { "Enabled": false },
  "Smtp": { "Enabled": false },
  "Swagger": { "Enabled": false }
}
```

非 Development 环境不会自动生成签名材料，即使设置 `CreateIfMissing=true` 也不会生成。活跃证书需要有效期内的 RSA 私钥，至少 2048 位；有 KeyUsage 扩展时须包含 `DigitalSignature`。签名证书用于 JWT 签名，TLS 入口证书需另行配置。

发布和运行示例：

```bash
dotnet publish src/NexusAuth.Host/NexusAuth.Host.csproj -c Release -o ./artifacts/host
cd artifacts/host
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5100 \
  dotnet NexusAuth.Host.dll
```

确认发布目录包含实际 `appsettings.Production.json`。容器可只读挂载为 `/app/appsettings.Production.json`，并挂载签名文件；也可参考 [`deploy/direct/sso.appsettings.example.json`](../../deploy/direct/sso.appsettings.example.json) 与 [`deploy/direct/compose.yml`](../../deploy/direct/compose.yml)，该部署模板挂载的是 `/app/appsettings.json`。代理后部署还需按第 6 节配置可信转发头。

数据库密码、签名密码、SMTP 授权码和管理员初始密码保存在受权限控制的部署 JSON 中，不要提交真实凭证。签名文件和 Data Protection key ring 都应持久化。

### 4.3 多密钥和旧配置兼容

`Jwt:SigningKeys` 非空时，整个数组优先于单密钥的 `SigningMode`、`SigningPath`、`SigningPassword` 及旧路径字段；数组每项使用自己的参数：

| 键 | 是否必填 | 说明 |
| --- | --- | --- |
| `Source` | 是 | 内置 `Certificate` 或 `RsaKeyFile` |
| `IsActive` | 必须恰好一项为 `true` | 活跃项用于签名，其他项用于验签 |
| `Path` | 是 | 密钥文件路径 |
| `Password` | 加密证书需要 | 此处不继承 `Jwt:SigningPassword` |
| `KeyId` | 可选 | 可自动推导；所有密钥 ID 不得重复 |
| `CreateIfMissing` | 可选，默认 `false` | 只有 Development 中的活跃项允许按此开关生成文件 |
| `Settings` | 可选 | 扩展签名源参数；两个内置源不要求填写 |

轮换配置示例：

```json
{
  "Jwt": {
    "SigningKeys": [
      {
        "Source": "Certificate",
        "IsActive": false,
        "KeyId": "signing-old",
        "Path": "/run/secrets/signing-old.pfx",
        "Password": "REPLACE_WITH_OLD_PASSWORD"
      },
      {
        "Source": "Certificate",
        "IsActive": true,
        "KeyId": "signing-new",
        "Path": "/run/secrets/signing-new.pfx",
        "Password": "REPLACE_WITH_NEW_PASSWORD"
      }
    ]
  }
}
```

重启后 JWKS 会包含所有已加载密钥的公钥。移除旧密钥前，应等待旧令牌过期和客户端 JWKS 缓存刷新；多实例切换活跃密钥前，应先让各实例及消费者能够识别新公钥。

未配置 `SigningKeys` 时，`SigningPath` 为空会回退到下列旧字段；新部署优先使用上文统一字段：

| 模式 | Development 回退 | 非 Development 回退 |
| --- | --- | --- |
| `Certificate` 路径 | `DevelopmentSigningCertificatePath`，默认 `App_Data/development-signing-certificate.pfx` | `SigningCertificatePath`，默认空，必须配置 |
| `Certificate` 密码 | `DevelopmentSigningCertificatePassword`，默认空 | `SigningCertificatePassword`，默认空 |
| `RsaKeyFile` 路径 | `DevelopmentSigningKeyPath`，默认 `App_Data/signing-key.json` | `SigningKeyPath`，默认空，必须配置 |

密码只在 `SigningPassword` 未设置或为 `null` 时回退；显式空字符串表示使用空密码。`RsaKeyFile` 是含 `KeyId`、`PrivateKeyPkcs8`（Base64 私钥）的 JSON 文件，不是 PEM 路径；该源对非活跃项也需要私钥文件。

## 5. 初始化管理员

```json
{
  "BootstrapAdmin": {
    "Username": "admin",
    "Password": "REPLACE_WITH_INITIAL_ADMIN_PASSWORD",
    "Nickname": "System Admin",
    "Email": "admin@example.com",
    "PromoteExistingUser": false
  }
}
```

用户名、密码必须一起设置或一起留空；`Nickname`、`Email` 可选。用户名不存在时创建系统管理员；已是系统账号时保留现有密码，修改配置不会重置密码。

如果同名普通账号已存在，默认拒绝启动。换一个管理员用户名，或确认已有账号可信后显式设置 `PromoteExistingUser=true`；该开关会将已有普通账号提升为管理员。

Host 不提供客户端、API 资源或 SCIM/Open API 凭证管理端点。业务接入前需通过 Workbench 或内部应用服务登记所需数据，见 [Workbench 使用手册](../../document/Workbench使用手册.md)。创建 Bootstrap 管理员不会自动创建 OAuth 客户端。

## 6. 反向代理、Cookie 和持久化

### 6.1 可信转发头

默认不接受 `X-Forwarded-*`。如果 HTTPS 在 Nginx、APISIX 等代理终止，在环境 JSON 中配置实际代理地址，例如：

```json
{
  "ClientIp": {
    "ForwardedHeadersEnabled": true,
    "KnownProxies": "10.10.0.8",
    "KnownNetworks": "",
    "ForwardLimit": 1
  },
  "Security": {
    "CookieSecurePolicy": "Always"
  }
}
```

- `KnownProxies` 是 IP 字符串；`KnownNetworks` 是 CIDR 字符串。多项用逗号或分号分隔，**不是 JSON 数组**。
- 启用后必须至少有一个可信代理或网段；`ForwardLimit` 范围为 `1–10`，默认 `1`。
- 代理应覆盖外部提交的转发头，并发送正确的 `X-Forwarded-For`、`X-Forwarded-Proto`；审计和限流使用处理后的客户端 IP。
- `Security:CookieSecurePolicy` 默认 `SameAsRequest`，只接受 `SameAsRequest` / `Always`；生产 HTTPS 设置 `Always`，本地 HTTP 保留默认值。
- `AllowedHosts` 基础默认 `*`；生产可配置实际域名，多项用分号分隔。

### 6.2 持久化目录

| 内容 | 默认或容器路径 | 用途 |
| --- | --- | --- |
| JWT 开发签名材料 | `App_Data`；容器 `/app/App_Data` | 保持重启前后 JWT 验签一致；生产也可单独挂载证书文件 |
| Data Protection key ring | 默认容器用户下 `/root/.aspnet/DataProtection-Keys` | 保护登录 Cookie、TOTP secret 和流程状态；必须保留和备份 |
| 日志 | `logs/nexusauth-sso-.log`；容器 `/app/logs` | 排错和日志留存 |

根 Compose 已持久化上述目录。Host 没有提供自定义 Data Protection 路径的 appsettings 配置节；更换容器用户时需要核对实际默认 key ring 位置。丢失旧 key ring 会导致 Cookie 失效和已有 TOTP secret 无法解密。

多实例需共享兼容的 Data Protection key ring、应用标识和签名材料；当前邮箱验证码及部分挑战防重放状态使用进程内缓存，共享数据库与 key ring 并不足以使这些状态跨实例共享。

## 7. 功能配置

本节默认值以基础 `appsettings.json` 为主，未列入基础文件的字段以代码默认值为准。环境覆盖差异见第 2 节。

### 7.1 SMTP 与邮箱验证码

`Smtp:Enabled=false` 时跳过 SMTP 字段校验，普通密码登录仍可使用。启用后，以下字段会在启动时校验：

| 键（`Smtp:` 前缀） | 当前值 | 必填或约束 |
| --- | --- | --- |
| `Enabled` | `false` | 邮件功能设为 `true` |
| `Host` | `smtp.qq.com`（示例） | 必须为实际 SMTP 主机 |
| `Port` | `465` | `1–65535`，与邮件服务的 TLS 方式对应 |
| `Security` | `SslOnConnect` | 只能精确填写 `SslOnConnect` 或 `StartTls` |
| `Username` | `test@qq.com`（示例） | 必填，实际发信账号 |
| `Password` | 空 | 必填，实际 SMTP 密码或服务商授权码 |
| `FromAddress` | `test@qq.com`（示例） | 必须为单个有效邮箱地址 |
| `FromName` | `NexusAuth` | 可选，发件人显示名称 |
| `VerificationCodeCooldownSeconds` | `60` | `1–3600` 秒，且不得超过验证码有效期 |
| `VerificationCodeLifetimeMinutes` | `5` | `1–60` 分钟 |

例如使用 QQ 邮箱的 SSL 端口：

```json
{
  "Smtp": {
    "Enabled": true,
    "Host": "smtp.qq.com",
    "Port": 465,
    "Security": "SslOnConnect",
    "Username": "YOUR_ACCOUNT@qq.com",
    "Password": "REPLACE_WITH_SMTP_AUTHORIZATION_CODE",
    "FromAddress": "YOUR_ACCOUNT@qq.com",
    "FromName": "NexusAuth",
    "VerificationCodeCooldownSeconds": 60,
    "VerificationCodeLifetimeMinutes": 5
  }
}
```

只把 `Enabled` 改为 `true` 而保留空密码，会导致启动校验失败。SMTP 关闭不代表页面入口全部隐藏，但实际发信不可用。验证码为 6 位数字，默认冷却 60 秒、有效 5 分钟；当前存储在进程内存中。

### 7.2 登录流程、会话和 TOTP

| 键 | 默认值 | 说明或有效范围 |
| --- | --- | --- |
| `LoginFlow:PendingStateLifetimeMinutes` | `5` | 待完成认证状态的有效期，`1–15` 分钟 |
| `LoginFlow:SessionLifetimeMinutes` | `1440` | 普通 SSO session 有效期，`5–1440` 分钟 |
| `LoginFlow:RememberMeLifetimeDays` | `3` | “记住我”有效期，`1–30` 天 |
| `LoginFlow:AllowRememberMe` | `true` | 是否允许“记住我”；关闭仍需保证上面的天数合法 |
| `LoginFlow:Steps` | `password/required`、`totp/conditional` | 认证步骤顺序；见下方规则 |
| `Totp:Issuer` | `NexusAuth` | 身份验证器显示的签发者名称，必须非空；与 JWT Issuer 是不同配置 |
| `Totp:EnrollmentLifetimeMinutes` | `10` | TOTP 待确认绑定有效期，`1–30` 分钟 |

默认步骤：

```json
{
  "LoginFlow": {
    "Steps": [
      { "Type": "password", "Requirement": "required" },
      { "Type": "totp", "Requirement": "conditional" }
    ]
  }
}
```

第一步必须且只能有一个 `password/required`；只支持 `password`、`totp`，类型不得重复。TOTP 的 `Requirement` 只支持 `required` 或 `conditional`：

- `conditional`：已绑定 TOTP 的用户需要输入动态码，未绑定的用户可以继续登录。
- `required`：未绑定 TOTP 的用户不能完成登录，账号中心也不允许关闭 TOTP；启用前先完成账号建档。
- 完全不配置 TOTP 步骤：登录流程不要求该因子。注意基础 JSON 已包含 TOTP 步骤，环境文件中缩短数组前需核对合并结果。

`Steps` 最终为空时，代码会补回默认两步。`Totp` 没有独立 `Enabled` 开关，配置始终参与启动校验；保留默认值即可。登录 Cookie 和数据库中的 SSO session 共同控制会话，不应将其有效期与 JWT 有效期混淆。

### 7.3 自注册与 Passkey / WebAuthn

`SelfRegistration:Enabled` 默认 `false`；关闭时注册页返回 404。开启后用户可自助建号，不强制依赖 SMTP；注册时填写邮箱不等同于已验证邮箱所有权。

启用 Passkey 的本地配置示例：

```json
{
  "WebAuthn": {
    "Enabled": true,
    "RelyingPartyId": "localhost",
    "RelyingPartyName": "NexusAuth",
    "Origins": ["http://localhost:5100"],
    "ChallengeLifetimeSeconds": 300,
    "UserVerification": "required"
  }
}
```

| 键（`WebAuthn:` 前缀） | 启用后的要求 |
| --- | --- |
| `RelyingPartyId` | 必须非空，填写域名，不带协议、端口或路径；生产示例 `sso.example.com` |
| `RelyingPartyName` | 必须非空，展示给用户的服务名称 |
| `Origins` | 非空数组，填写浏览器实际 Origin（协议、主机及端口），不带业务路径；生产示例 `https://sso.example.com` |
| `ChallengeLifetimeSeconds` | `60–900` 秒，默认 `300` |
| `UserVerification` | 默认 `required`，要求认证器执行用户验证；建议保留 |

`Enabled=false` 时跳过 RP 和 Origin 等专用校验；启用后由浏览器和 FIDO2 库进一步验证 RP 与 Origin 的匹配关系。非 localhost 环境使用 HTTPS。此开关与自注册独立，已有用户也可在账号中心绑定 Passkey。

### 7.4 滑块验证码

| 键（`SliderCaptcha:` 前缀） | 默认值 | 约束 |
| --- | --- | --- |
| `Enabled` | `false` | 开启后用于登录、注册和找回密码等操作 |
| `ChallengeLifetimeSeconds` | `120` | `1–3600` 秒 |
| `TolerancePixels` | `5` | 大于等于 `0` 且小于 `TrackWidthPixels` |
| `TrackWidthPixels` | `300` | `1–2000` 像素 |

即使关闭滑块，数值约束也会参与启动校验。

### 7.5 微信小程序登录

默认关闭。启用时在环境 JSON 中增加：

```json
{
  "WeChat": {
    "MiniProgram": {
      "Enabled": true,
      "ClientId": "wechat-miniapp",
      "AppId": "REPLACE_WITH_WECHAT_APP_ID",
      "AppSecret": "REPLACE_WITH_WECHAT_APP_SECRET",
      "Code2SessionEndpoint": "https://api.weixin.qq.com/sns/jscode2session",
      "AutoProvisionUsers": true
    }
  }
}
```

启用后 `ClientId`、`AppId`、`AppSecret` 必须非空；上游地址必须是绝对 HTTPS URL，且不包含查询串、片段或用户信息。`AutoProvisionUsers` 默认 `true`，首次登录时创建本地用户；关闭后需要预先存在对应的外部身份关联。

还需在数据库登记与 `ClientId` 一致的 public client，认证方法为 `none`，允许 `urn:nexusauth:grant-type:wechat_miniprogram` 授权类型。微信 AppSecret 仅保存在服务端；详细接入流程见[微信小程序接入](../../document/微信小程序接入.md)。

### 7.6 安全名单与锁定

| 键（`Security:` 前缀） | 默认值 | 用途 |
| --- | --- | --- |
| `ClientWhitelist` / `ClientBlacklist` | `[]` | 按客户端 ID 匹配 |
| `UserWhitelist` / `UserBlacklist` | `[]` | 按用户名或用户 ID（GUID 字符串）匹配 |
| `LoginFailureLimit` | `5` | 失败次数阈值 |
| `LoginLockoutMinutes` | `5` | 账号锁定时长，单位分钟 |
| `CookieSecurePolicy` | `SameAsRequest` | Cookie Secure 策略，见第 6 节 |

名单为字符串数组，匹配忽略大小写，黑名单优先。白名单非空后，只允许名单内对象。锁定数值没有独立启动范围校验，部署时保持正数。

### 7.7 数据清理

| 键（`DataRetention:` 前缀） | 默认值 | 约束与用途 |
| --- | --- | --- |
| `Enabled` | `true` | 是否运行后台清理 |
| `SweepIntervalMinutes` | `5` | `1–1440` 分钟，每轮间隔 |
| `ExpiredDataRetentionDays` | `7` | `0–3650` 天，过期认证数据额外保留期 |
| `LoginAuditRetentionDays` | `180` | `1–36500` 天，登录审计保留期 |
| `CleanupBatchSize` | `1000` | `1–100000`，每批最大记录数 |
| `MaxBatchesPerTypePerSweep` | `20` | `1–10000`，每类数据每轮最大批数 |

清理涉及授权码、刷新令牌、设备授权、令牌黑名单、WebAuthn 挑战、SSO session、登录审计和授权同意请求。第一次清理在首个周期之后执行；即使 `Enabled=false`，其余数值仍会进行启动校验。

### 7.8 页面、日志、SCIM 与 Swagger

| 配置 | 默认值或行为 | 说明 |
| --- | --- | --- |
| `LoginPage:BrandName` | `NexusAuth` | 品牌名称 |
| `LoginPage:BrandLogoUrl` | `/brand/nexusauth-logo.svg` | Logo 地址；相对静态路径须有实际资源 |
| `LoginPage:MarketingHeading` / `MarketingDescription` | 基础 JSON 中的中文说明 | 登录页宣传标题和描述 |
| `LoginPage:LoginTitle` / `LoginSubtitle` | 基础 JSON 中的英文文案 | 登录表单标题和说明 |
| `Logging:LogLevel` | `Default=Information` | ASP.NET Core 日志配置 |
| `LuckLogging` | 文件日志及级别覆盖 | Host 使用 `Luck.Logging.Serilog`；文件参数见下方 |
| `Scim:Enabled` | `true` | 关闭后不注册 SCIM 控制器，相关接口返回 404 |
| `Swagger:Enabled` | 未设置时跟随环境 | Development 开启，其他环境关闭；显式 `true` / `false` 优先 |

`LuckLogging` 的当前基础配置为：`Module=NexusAuth.SSO`、`FilePath=logs/nexusauth-sso-.log`、`MinimumLevel=Information`、`FileSizeLimitBytes=104857600`（100 MiB）、`RetainedFileCountLimit=30`、`RollOnFileSizeLimit=true`、`Shared=true`、`FlushIntervalSeconds=1`。`MinimumLevelOverrides` 按基础配置将 Microsoft、EF 数据库命令、System、Npgsql 等日志降至 Warning，并保留宿主生命周期 Information。确认进程可写入日志目录即可，通常无需覆盖这些值。

## 8. 启动后如何验证

使用本地默认地址时，可以先检查协议元数据和公钥：

```bash
curl --fail --silent --show-error http://localhost:5100/.well-known/openid-configuration
curl --fail --silent --show-error http://localhost:5100/.well-known/jwks.json
```

检查返回的 `issuer`、各协议端点地址与实际访问地址一致，并确认 JWKS 包含预期的 `kid`。这两项验证 HTTP 和签名材料，不代表数据库和完整登录链路已经通过验证；还应实际登录并访问账号中心。

| 入口 | 用途 | 前提 |
| --- | --- | --- |
| `/account/login` | 用户登录 | 已有账号；邮件或 Passkey 模式还需启用对应功能 |
| `/account/register` | 自助注册 | `SelfRegistration:Enabled=true` |
| `/account` | 账号安全中心 | 登录后访问 |
| `/account/email` | 邮箱管理 | 发验证码需要 SMTP |
| `/account/two-factor` | 绑定/管理 TOTP | 登录后访问 |
| `/account/passkeys` | 管理 Passkey | 启用 WebAuthn |
| `/device` | 设备授权确认页 | 设备授权流程 |
| `/.well-known/openid-configuration` | OIDC Discovery | 配置正确的 Issuer |
| `/.well-known/jwks.json` | 公钥列表 | 正确加载签名材料 |
| `/connect/authorize`、`/connect/token` | 授权及获取令牌 | 已登记客户端、回调和授权类型 |
| `/connect/userinfo`、`/connect/introspect`、`/connect/revocation`、`/connect/endsession` | 用户信息、自省、撤销和退出 | 按对应协议提供令牌或客户端认证 |
| `/connect/deviceauthorization` | 发起设备授权 | 已登记允许设备授权的客户端 |
| `/scim/v2/*` | SCIM 用户供给 | SCIM 开关及独立供给凭证 |
| `/openapi/v1/applications`、`/openapi/v1/service-resources` | 只读目录查询 | 独立 Open API 消费凭证 |
| `/swagger`、`/swagger/v1/swagger.json` | 接口文档 | Development 默认启用，其他环境需显式开启 |

SCIM 和 Open API 的调用凭证通过 Workbench 管理，不是给任意登录用户直接使用的管理接口。只启用功能开关不会自动创建凭证。

## 9. 常见问题

| 现象 | 优先检查 |
| --- | --- |
| 环境变量改了，数据库/JWT/SMTP 没变 | Host 只读取 JSON 业务配置；检查当前环境文件和实际挂载文件，修改后重启 |
| 源码配置改了，容器没变化 | Compose 挂载文件会覆盖对应环境文件；修改 `docker/host.appsettings.json` 或自定义挂载文件 |
| Production 报签名路径为空或文件不存在 | 配置 `Jwt:SigningPath` 或完整 `SigningKeys`，并挂载预先准备的签名文件 |
| 证书密码错误，改密码后仍不能启动 | 配置密码必须匹配已有 PFX；修改配置不会重新生成已有文件 |
| Discovery 能打开，但登录报数据库错误 | 检查连接串、数据库连通性和表结构；Discovery 不查询数据库 |
| 数据库连接成功但报 relation 不存在 | 核对目标数据库、`Search Path=nexusauth` 及 schema 是否初始化；已有卷不会再次执行初始化钩子 |
| BootstrapAdmin 导致启动失败 | 用户名和密码必须成对配置；同名普通账号默认不能自动提升 |
| 修改 Bootstrap 密码没有改变账号密码 | Bootstrap 只负责创建，不重置已有系统账号的密码 |
| Passkey 无法注册或登录 | 核对 `Enabled`、RP ID、Origin 的协议/域名/端口；源码默认 Origin 是 `5200`，与默认监听 `5100` 不同 |
| SMTP 启用后启动失败 | 按第 7 节补齐 SMTP 字段并检查端口、安全模式、冷却和有效期约束 |
| Production 访问 Swagger 返回 404 | 默认关闭；确需开放时在环境 JSON 设置 `Swagger:Enabled=true` |
| 关闭 SCIM 后返回 404 | 预期行为，`Scim:Enabled=false` 会移除控制器注册 |
| 代理后所有请求记录为同一个 IP | 配置 `ClientIp` 可信代理和转发头；不能仅设置请求头而不配置可信来源 |
| 本地 HTTP 无法保持登录 | 检查是否误设置 `Security:CookieSecurePolicy=Always`，本地 HTTP 使用 `SameAsRequest` |
| 重启后旧 Cookie 或 TOTP 不可用 | 检查 Data Protection key ring 是否保留、权限及实例间配置是否一致 |

## 10. 相关文档与配置依据

- [Host 完整使用手册](../../document/NexusAuth.Host使用手册.md)：协议流程、用户功能、SCIM 与 Open API 细节。
- [完整配置键示例](../../document/appsettings.Host.complete.example.json)：含未写入基础 JSON 的配置字段。
- [SMTP 配置与发信验证](../../document/SMTP发信试验.md)。
- [微信小程序接入](../../document/微信小程序接入.md)。
- [Workbench 使用手册](../../document/Workbench使用手册.md)：客户端、API 资源和消费凭证管理。
- 配置入口：[`Program.cs`](Program.cs)、[`AppWebModule.cs`](AppWebModule.cs)、[`BootstrapAdminHostedService.cs`](BootstrapAdminHostedService.cs)。
- JWT：[`JwtOptions.cs`](../NexusAuth.Application/Services/Tokens/JwtOptions.cs)、[`TokenSigningCredentialsProvider.cs`](../NexusAuth.Application/Services/Tokens/TokenSigningCredentialsProvider.cs)。
- 数据库：[`EntityFrameworkCoreModule.cs`](../NexusAuth.Persistence/EntityFrameworkCoreModule.cs)。
