# Docker 本地预览

在仓库根目录运行：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d --build
```

入口：

- Workbench 管理台：http://localhost:5560
- 个人中心：http://localhost:5100/account
- Passkey 管理：http://localhost:5100/account/passkeys

本地预览默认不创建管理员。需要登录 Workbench 时，在 Host 的 appsettings 中配置 `BootstrapAdmin` 的用户名和密码，或使用数据库中已有的管理员；如果账号已经存在，保留其现有密码。

本地预览已开启拖动图片验证码，提交账号密码后会弹出拼图验证。

登录来源 IP 的可信代理配置和 Docker Desktop NAT 限制见 [CLIENT-IP.md](CLIENT-IP.md)。
邮箱验证码、邮箱登录及找回密码的 SMTP 配置见 [EMAIL-VERIFICATION.md](EMAIL-VERIFICATION.md)。

可选 `docker-compose.local.yml` 在新数据卷初始化三个演示账号：

| 用户名 | 密码 | 状态 |
| --- | --- | --- |
| demo.alice | DemoPass123! | 启用 |
| demo.bob | DemoPass123! | 启用 |
| demo.disabled | DemoPass123! | 停用，不能登录 |

管理员登录 Workbench 后，通过“用户管理 → 详情”查看用户资料和凭据信息。普通用户从个人中心添加自己的 Passkey。初始化不伪造 Passkey 或 TOTP，绑定需要真实设备完成。浏览器必须使用 `localhost`，以匹配当前 Passkey RP ID 和 Origin。

Host 的 Issuer 配置在 `docker/host.local.appsettings.json`，Workbench 的 Authority 默认通过 `NEXUSAUTH_JWT_ISSUER` 指定。容器内的 `localhost` 指向容器自身，不能用于 `admin-api` 访问 `sso`。容器化预览启动前，应将两处设置为浏览器和 `admin-api` 都能通过同一 DNS/IP 访问、并路由到 Provider 的同一个地址。Dashboard Nginx 通过 Compose DNS 转发到 `admin-api:8080`。

初始化脚本只在新数据库卷上自动执行。已有本项目数据卷可单独添加演示数据，重复执行保留已有账号与密码：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml exec -T db psql -U nexusauth -d nexusauth -v ON_ERROR_STOP=1 -f /docker-entrypoint-initdb.d/010-local-demo.sql
```

数据库、签名材料和 Data Protection 密钥均持久化在项目命名卷中。常规停止使用 `docker compose -f docker-compose.yml -f docker-compose.local.yml stop`，保留数据。HTTP 入口仅绑定本机回环地址。本地演示密码与固定 SCIM 测试凭据仅用于此预览环境。
