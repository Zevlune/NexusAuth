# 邮箱验证码

入口：个人中心 `/account/email`，邮箱验证码登录 `/account/login?mode=email`，找回密码 `/account/forgot-password`。

账号先使用原密码登录个人中心，验证邮箱所有权。绑定/更换邮箱需验证当前密码；账号已绑定 TOTP 时还需动态码。验证成功后撤销现有会话和刷新令牌，请重新登录。只有已绑定邮箱、启用且未锁定的账号可使用邮箱验证码登录和密码找回；已配置的登录 TOTP 仍会执行，密码找回也不会绕过已绑定的 TOTP。

本地 admin 收件邮箱：`18790997531@163.com`。沿用账号已有邮箱作为绑定邮箱；个人中心新增或更换邮箱时必须收到验证码后才能保存。

## 发件配置

复用现有 SMTP 实现。Compose 从映射到容器 `/app/appsettings.json` 的 `Smtp` 节点读取 SMTP 参数。部署前确认 `Enabled` 为 `true`，并将 `Username` 和 `FromAddress` 设置为真实发件邮箱；服务器、端口、安全模式和显示名也在该节点配置。镜像内置的 `src/NexusAuth.Host/appsettings.json` 示例使用 `smtp.qq.com:465` 和 `SslOnConnect`。运行时不读取 `smtp.qq.local.json`。SMTP 授权码不得写入配置文件、仓库或镜像。

Compose 只通过环境变量注入 SMTP 授权码，可在仓库根目录 `.env` 中设置：

```dotenv
NEXUSAUTH_SMTP_PASSWORD=发件邮箱的SMTP授权码
```

程序仍兼容 `Smtp__Password` 等标准配置环境变量，但 Compose 的 SMTP 环境变量只保留 `NEXUSAUTH_SMTP_PASSWORD`；其他 SMTP 参数应修改映射的 `/app/appsettings.json` 中的 `Smtp` 节点。支持 QQ、163 及其他标准 SMTP 服务商；安全模式只允许 `SslOnConnect` 或 `StartTls`，端口应按邮件服务商要求配置。收件地址不能替代发件账号与授权码。

SMTP 启用时，授权码是必需配置。如果未提供 `NEXUSAUTH_SMTP_PASSWORD` 或 `Smtp__Password`，启动时配置校验会失败。

发送冷却和有效期统一配置在映射的 `/app/appsettings.json` 的 `Smtp` 节点中：

```json
"Smtp": {
  "VerificationCodeCooldownSeconds": 60,
  "VerificationCodeLifetimeMinutes": 5
}
```

`VerificationCodeCooldownSeconds` 是按钮倒计时和后端发送间隔，单位秒，范围 1～3600；`VerificationCodeLifetimeMinutes` 是验证码有效期，单位分钟，范围 1～60。冷却不得超过验证码有效期。程序仍兼容对应的环境变量配置，但 Compose 部署通过映射的配置文件设置这些值。

后端通过配置监视器读取时间参数，挂载文件修改并被 .NET 检测到后，新发送使用新值，刷新页面可加载新倒计时设置。已有验证码的到期时间和已开始的冷却不会因改配置而延长或缩短。修改文件中的 SMTP 参数后，按部署流程重建容器。

更新环境变量后重建容器配置：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d --build sso
```

当前部署默认配置使用 QQ SMTP，密码不会放入镜像或返回给前端。发送失败不会返回验证码，验证码、授权码和邮件正文不输出到日志；公共发送接口统一返回不泄露账号是否存在的提示。

## 服务复用

`NexusAuth.Shared.Email` 提供 `IEmailSender`、SMTP 配置和 MailKit SMTP 实现及 `AddNexusAuthSmtp(configuration)` 注册入口；`NexusAuth.Application.Services.Email` 提供验证码发送/消费服务、邮箱账号业务及 `AddNexusAuthEmail(configuration)` 注册入口（内部注册 Shared 发信能力）。Host 和 Workbench API 均注册了通用邮件能力，其他业务服务可注入 `IEmailSender` 发送通知或密码重置邮件，不依赖 Host 页面项目。

Workbench 是独立进程，需在其配置中设置 `Smtp` 或向其注入 SMTP 环境变量；注册服务不等于启用发信。管理员密码重置仍通过已有权限校验后的业务入口执行。验证码缓存目前属于各进程，若后台发出的验证码需要在 Host 校验，需要先接入共享存储，不能直接用两个进程各自的内存缓存。

## 发送日志

Host 记录 SMTP 失败为 Error，包含异常详情、服务器、端口、安全模式和失败阶段：`connect-tls`（连接或 TLS）、`authenticate`（账号或授权码认证）、`send`（邮件提交）。SMTP 接收成功记录 Information；这代表服务器接受邮件，不保证最终进入收件箱。发送冷却、SMTP 禁用等拒绝记录 Warning，账号不可用导致跳过发送记录 Information。

Compose 查看：`docker compose logs --tail=200 sso`。默认文件日志在容器 `/app/logs/nexusauth-sso-*.log`，本地 Compose 映射为宿主机 `logs/sso/`。宿主机配置可通过 `LuckLogging:FilePath` 修改，日志级别须允许 Warning/Error。

## 数据库升级

1.0.3 不再使用或映射邮箱验证时间字段，直接使用已有 `email` 字段，旧数据库无需增加字段。已有邮箱视为绑定邮箱，登录和找回密码仍必须验证邮件验证码。

如果曾在 1.0.2 中添加过验证时间字段，待所有服务升级后，可执行 `docker/002-email-verification.sql` 清理；未加过该字段的数据库无需执行任何升级 SQL。

验证码六位、单次使用、最多五次错误，每小时最多10封。默认有效期5分钟、发送冷却60秒，均由上述配置决定。发送按钮倒计时并禁用，刷新、切换页面和同浏览器其他标签页继续计时。同用途、同账号密码版本存在未过期且未失效的验证码时，冷却结束后也不会重复发送或替换原验证码；请使用已有验证码，过期后才可重新发送。个人中心显示具体拒绝原因；匿名入口统一提示已发送或存在有效验证码，避免暴露账号存在性。验证码按用途和账号密码版本隔离，验证码登录/绑定/找回不能互换，修改密码使原验证码失效。匿名发送与登录保留图片验证、CSRF 校验和 IP 限流。

当前验证码与限流状态存储在单实例内存缓存。服务重启后旧验证码失效；多实例部署时需要共享存储和原子消费，当前本地 Compose 是单实例。
