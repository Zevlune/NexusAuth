# Host SMTP 配置与发信验证

邮件能力属于 [NexusAuth.Host](NexusAuth.Host使用手册.md)。Workbench + Dashboard 的部署与管理操作见 [Workbench 使用手册](Workbench使用手册.md)。

## 1. 当前实现与启动前提

Host 已通过 `AddNexusAuthSmtp` 注册 `SmtpOptions`、启动校验、`IEmailSender` 和 MailKit SMTP 传输。邮箱验证码已经接入登录、个人邮箱管理和找回密码。注册表单填写邮箱不会自动完成邮箱验证。

Host 从 [`appsettings.json`](../src/NexusAuth.Host/appsettings.json) 读取 `Smtp` 节点，再读取 `appsettings.{环境}.json` 并使用其中的同名字段覆盖基础配置。`{环境}` 对应启动时的 `ASPNETCORE_ENVIRONMENT`，例如 Development 环境读取 `appsettings.Development.json`。Host 通过 `Smtp.Enabled` 控制邮件能力：默认 `false`，未配置邮件服务时可以直接启动；改为 `true` 后，如果缺少 Host、用户名、密码或有效发件人，`ValidateOnStart` 会阻止 Host 启动。关闭 SMTP 后，邮件发送和验证码流程不可用。

Host 的 SMTP 参数不使用 `NEXUSAUTH_SMTP_*` 或 `Smtp__*` 环境变量。配置写在本进程实际读取的 `appsettings.json` 或对应的 `appsettings.{环境}.json` 中；后者适合保存部署环境的值。

## 2. 源码运行时的配置

- 基础默认值放在 `appsettings.json`。
- 开发、测试或生产环境的覆盖值放在对应的 `appsettings.{环境}.json`。
- 启用前替换示例中的邮箱和 SMTP 授权码；密码通常使用邮箱服务商生成的 SMTP 授权码，配置文件中的实际密码不要提交到仓库。

`appsettings.{环境}.json` 中的最小启用示例：

```json
{
  "Smtp": {
    "Enabled": true,
    "Host": "smtp.qq.com",
    "Port": 465,
    "Security": "SslOnConnect",
    "Username": "your-account@qq.com",
    "Password": "REPLACE_WITH_SMTP_AUTHORIZATION_CODE",
    "FromAddress": "your-account@qq.com",
    "FromName": "NexusAuth",
    "VerificationCodeCooldownSeconds": 60,
    "VerificationCodeLifetimeMinutes": 5
  }
}
```

| `Smtp` 配置 | 校验与含义 |
| --- | --- |
| `Enabled` | `false` 时跳过连接配置校验，发送服务拒绝发信 |
| `Host` / `Port` | Host 必填；端口范围 `1..65535` |
| `Security` | 只接受 `SslOnConnect` 或 `StartTls`；按服务商配置选择，常用端口分别为 465 / 587 |
| `Username` / `Password` | 启用时均必填 |
| `FromAddress` / `FromName` | 发件地址必填，必须是单个合法邮箱；名称用于邮件显示 |
| `VerificationCodeCooldownSeconds` | 发送冷却，范围 `1..3600` 秒，默认 60 |
| `VerificationCodeLifetimeMinutes` | 验证码有效期，范围 `1..60` 分钟，默认 5；不得短于冷却时间 |

修改配置后重新启动 Host。运行容器时，需要确保更新后的 `appsettings.json` 或 `appsettings.{环境}.json` 已进入镜像，或通过卷覆盖容器 `/app` 下的对应文件。

## 3. Docker Compose 与直接部署

Host 的 Compose 服务直接使用镜像内 `/app/appsettings.json` 和 `/app/appsettings.{环境}.json`，环境文件中的 `ASPNETCORE_ENVIRONMENT` 决定加载哪个覆盖文件。Host 的 `.env`、`sso.env` 和 Compose `environment` 不再配置 SMTP 参数。

启用邮件时，需要同时提供真实 SMTP 主机、用户名、密码和发件地址。本地开发可直接编辑上面的 JSON 配置文件；需要把配置留在容器外时，可在 Compose 覆盖文件中把对应配置文件只读挂载到 `/app`，例如：

```yaml
services:
  sso:
    environment:
      ASPNETCORE_ENVIRONMENT: Production
    volumes:
      - ./appsettings.Production.json:/app/appsettings.Production.json:ro
```

`ASPNETCORE_ENVIRONMENT` 和挂载文件名必须使用同一个环境名称，且配置文件权限应仅允许部署账号读取。示例中的占位值不能直接用于实际发信。

```bash
docker compose -f docker-compose.yml -f docker-compose.smtp.local.yml up -d db sso
```

不启用邮件时，保持 `Smtp.Enabled=false` 即可，不需要创建 SMTP 覆盖文件或 Host SMTP 环境变量。直接部署的 [`sso.env.example`](../deploy/direct/sso.env.example) 同样只保留非 SMTP 环境配置。

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
| Host 无法启动，提示 SMTP 配置缺失 | 仅当 `Smtp.Enabled=true` 时发生；补全配置文件或改回 `false` |
| 修改配置后行为不变 | 检查 `ASPNETCORE_ENVIRONMENT`、配置文件名、容器挂载路径，并重启 Host 容器 |
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
- [Host SMTP 注册](../src/NexusAuth.Host/Program.cs)
