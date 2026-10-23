# Host SMTP 配置与发信验证

邮件能力属于 [NexusAuth.Host](NexusAuth.Host使用手册.md)。Workbench + Dashboard 的部署与管理操作见 [Workbench 使用手册](Workbench使用手册.md)。

## 1. 当前实现与启动前提

Host 已通过 `AddNexusAuthSmtp` 注册 `SmtpOptions`、启动校验、`IEmailSender` 和 MailKit SMTP 传输。邮箱验证码已经接入登录、个人邮箱管理和找回密码。注册表单填写邮箱不会自动完成邮箱验证。

当前 [`appsettings.json`](../src/NexusAuth.Host/appsettings.json) 默认设置 `Smtp.Enabled=false`，因此未配置邮件服务的本地环境可以直接启动。只有显式启用时才会连接 SMTP；启用后如果缺少 Host、用户名、密码或有效发件人，`ValidateOnStart` 会阻止 Host 启动。

本地只需要密码或 Passkey 登录时无需额外关闭邮件。若要确认运行进程中的值，可以设置：

```bash
export NEXUSAUTH_SMTP_ENABLED=false
```

然后按照 [Host 手册](NexusAuth.Host使用手册.md) 配置数据库、BootstrapAdmin 等并启动。关闭 SMTP 后，邮件发送和验证码流程不可用。

## 2. 源码运行时的配置

下列变量由 Host 的 [`Program.cs`](../src/NexusAuth.Host/Program.cs) 显式映射到配置。替换示例中的邮箱、SMTP 授权码，再在同一终端启动 Host；SMTP 密码通常是邮箱服务商的 SMTP 授权码。

```bash
export NEXUSAUTH_SMTP_ENABLED=true
export NEXUSAUTH_SMTP_HOST=smtp.qq.com
export NEXUSAUTH_SMTP_PORT=465
export NEXUSAUTH_SMTP_SECURITY=SslOnConnect
export NEXUSAUTH_SMTP_USERNAME=your-account@qq.com
export NEXUSAUTH_SMTP_PASSWORD='replace-with-smtp-authorization-code'
export NEXUSAUTH_SMTP_FROM_ADDRESS=your-account@qq.com
export NEXUSAUTH_SMTP_FROM_NAME=NexusAuth
export NEXUSAUTH_SMTP_VERIFICATION_CODE_COOLDOWN_SECONDS=60
export NEXUSAUTH_SMTP_VERIFICATION_CODE_LIFETIME_MINUTES=5
```

| 配置 | 校验与含义 |
| --- | --- |
| `Enabled` | `false` 时跳过连接配置校验，发送服务拒绝发信 |
| `Host` / `Port` | Host 必填；端口范围 `1..65535` |
| `Security` | 只接受 `SslOnConnect` 或 `StartTls`；按服务商配置选择，常用端口分别为 465 / 587 |
| `Username` / `Password` | 启用时均必填 |
| `FromAddress` / `FromName` | 发件地址必填，必须是单个合法邮箱；名称用于邮件显示 |
| `VerificationCodeCooldownSeconds` | 发送冷却，范围 `1..3600` 秒，默认 60 |
| `VerificationCodeLifetimeMinutes` | 验证码有效期，范围 `1..60` 分钟，默认 5；不得短于冷却时间 |

也可使用标准配置键，例如 `Smtp__Enabled=false`。同一键同时配置 Host 专用 `NEXUSAUTH_SMTP_*` 变量时，专用变量的显式映射会覆盖该值。

## 3. Docker Compose 的环境变量

根 [`docker-compose.yml`](../docker-compose.yml) 的 `sso.environment` 显式转发 `NEXUSAUTH_SMTP_ENABLED`，默认值为 `false`，同时转发 `NEXUSAUTH_SMTP_PASSWORD`。因此宿主 `.env` 中的 `NEXUSAUTH_SMTP_ENABLED=true` 会进入容器并启用邮件；其余邮件配置仍读取镜像内 appsettings 或 Compose override。

启用邮件时，需要同时提供真实 SMTP 主机、用户名、密码和发件地址。推荐在仓库根目录创建本地覆盖文件 `docker-compose.smtp.local.yml`：

```yaml
services:
  sso:
    environment:
      NEXUSAUTH_SMTP_ENABLED: "true"
      NEXUSAUTH_SMTP_HOST: "${NEXUSAUTH_SMTP_HOST:-smtp.qq.com}"
      NEXUSAUTH_SMTP_PORT: "${NEXUSAUTH_SMTP_PORT:-465}"
      NEXUSAUTH_SMTP_SECURITY: "${NEXUSAUTH_SMTP_SECURITY:-SslOnConnect}"
      NEXUSAUTH_SMTP_USERNAME: "${NEXUSAUTH_SMTP_USERNAME:?SMTP username is required}"
      NEXUSAUTH_SMTP_PASSWORD: "${NEXUSAUTH_SMTP_PASSWORD:?SMTP password is required}"
      NEXUSAUTH_SMTP_FROM_ADDRESS: "${NEXUSAUTH_SMTP_FROM_ADDRESS:?SMTP sender is required}"
      NEXUSAUTH_SMTP_FROM_NAME: "${NEXUSAUTH_SMTP_FROM_NAME:-NexusAuth}"
      NEXUSAUTH_SMTP_VERIFICATION_CODE_COOLDOWN_SECONDS: "60"
      NEXUSAUTH_SMTP_VERIFICATION_CODE_LIFETIME_MINUTES: "5"
```

在当前终端设置上一节的实际值，然后执行以下命令；其他 Host 配置仍按主手册准备：

```bash
docker compose -f docker-compose.yml -f docker-compose.smtp.local.yml up -d --build db sso
```

不创建覆盖文件时，根 Compose 会以 `NEXUSAUTH_SMTP_ENABLED=false` 启动 Host；部署整套 Workbench 时同样不需要仅为关闭 SMTP 增加本地覆盖。

## 4. 页面使用步骤

### 邮箱绑定、变更与解绑

1. 登录 Host，进入 `/account/email`。
2. 输入目标邮箱、当前密码，以及账号要求的 TOTP，发送验证码。
3. 输入验证码并提交，完成绑定或变更。
4. 解绑也需要当前密码和适用的 TOTP。成功变更或解绑会撤销已有会话，需要重新登录。

### 邮箱验证码登录

1. 在 Host `/account/login` 切换到邮箱登录，输入已验证邮箱。
2. 发送并填写验证码；绑定 TOTP 的账号继续完成第二因素验证。
3. 公开发码入口对不存在或不可用账号返回统一提示，页面提示成功不代表一定发信。

### 忘记密码

1. 打开 `/account/forgot-password`，填写已验证邮箱，按页面要求完成滑块校验并发码。
2. 输入验证码、新密码，以及账号要求的 TOTP。
3. 重置成功后重新登录；已有令牌和会话会被撤销。

当前验证码为 6 位数字，按 `verify-email`、`reset-password`、`login` 区分用途。发送存在冷却和每小时发送上限，验证码最多允许 5 次错误校验；有效验证码在冷却结束后仍有效时不会重复发出。验证码状态保存在进程内存中，进程重启后失效，多实例部署需要考虑请求落到同一实例。

## 5. 验证与排错

先检查启动日志中的 OptionsValidationException，再检查 SMTP 连接、TLS 和认证日志。邮件服务记录服务器、端口、TLS 模式和失败阶段，避免将密码、验证码或正文作为诊断内容。服务端确认提交也不保证邮件已进入收件箱，应同时检查垃圾箱和服务商投递策略。

常见问题：

| 现象 | 检查方向 |
| --- | --- |
| Host 无法启动，提示 SMTP 配置缺失 | 仅当显式启用 SMTP 时发生；补全配置或设置 `NEXUSAUTH_SMTP_ENABLED=false` |
| 修改 `.env` 后行为不变 | 检查变量是否在 Compose 的 `environment` 中显式传递，容器是否已重建 |
| 认证失败 | SMTP 服务是否开启；使用授权码；用户名是否为完整邮箱 |
| TLS 连接失败 | 核对服务商要求的端口与 `Security`，避免混用 465 / StartTls |
| 页面显示已发送但没有邮件 | 核对账号是否存在、启用且邮箱已验证；检查冷却、上限、SMTP 日志及收件箱 |
| 验证码突然失效 | 检查有效期、用途、错误次数、进程重启及多实例请求分配 |

仓库包含不发送真实邮件的验证测试，可在根目录运行：

```bash
dotnet test tests/NexusAuth.Host.IntegrationTests/NexusAuth.Host.IntegrationTests.csproj --filter 'FullyQualifiedName~EmailVerificationCodeServiceTests|FullyQualifiedName~EmailServiceRegistrationTests|FullyQualifiedName~EmailAccountServiceSecurityTests'
dotnet test tests/NexusAuth.Host.IntegrationTests/NexusAuth.Host.IntegrationTests.csproj --filter 'FullyQualifiedName~SmtpEmailSenderTests&FullyQualifiedName!~Configured_live_qq_smtp_can_send_test_message'
```

[`SmtpEmailSenderTests`](../tests/NexusAuth.Host.IntegrationTests/SmtpEmailSenderTests.cs) 另有真实发信测试 `Configured_live_qq_smtp_can_send_test_message`：它读取 `smtp.qq.local.json` 的 `Username`、`AuthorizationCode`，且收件地址硬编码在测试中。需要实际投递时，先核对并改成自己的收件地址，再显式选择该测试。缺少本地配置时它会输出“未发送”并返回，不能把测试通过理解成已投递。

## 6. 对照代码

- [SMTP 配置与校验](../src/NexusAuth.Shared/Email/SmtpOptions.cs)
- [SMTP 服务注册](../src/NexusAuth.Shared/Email/EmailServiceCollectionExtensions.cs)
- [发送服务](../src/NexusAuth.Shared/Email/SmtpEmailSender.cs)
- [真实 SMTP 传输](../src/NexusAuth.Shared/Email/MailKitSmtpTransport.cs)
- [验证码服务](../src/NexusAuth.Application/Services/Email/EmailVerificationCodeService.cs)
- [账号邮箱业务](../src/NexusAuth.Application/Services/Email/EmailAccountService.cs)
- [Host 环境变量映射](../src/NexusAuth.Host/Program.cs)
