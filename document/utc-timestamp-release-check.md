# UTC 时间入库修复与上线验证

验证日期：2026-10-13。

本次检查针对 SCIM 创建密钥报错所暴露的同类时间写入问题，覆盖 Host、Workbench API、领域实体及 PostgreSQL 仓储。结论适用于当前代码和隔离测试环境，不代表已验证真实生产配置、外部服务或负载。

## 修复范围

| 功能 | 修复点 |
| --- | --- |
| SCIM 密钥 | 创建、编辑过期时间，以及显式使用、撤销时间转 UTC |
| Open API 密钥 | 实体创建、编辑过期时间及使用时间转 UTC；服务层原有转换保留 |
| Passkey | 凭据创建、认证、禁用时间及挑战创建、到期、消费时间转 UTC |
| TOTP | 待确认有效期、创建、确认、使用、禁用时间转 UTC；原生 SQL 参数单独转换 |
| 用户登录 | 失败登录锁定期限、成功登录及令牌失效操作的更新时间转 UTC；令牌失效截止仍按秒截断 |
| OAuth / 设备授权 | 授权码认证时间、设备轮询时间转 UTC |
| SSO / 令牌撤销 | 会话撤销时间和令牌黑名单过期时间转 UTC |
| 所有 EF 时间字段 | `ConfigureConventions` 注册 `UtcDateTimeOffsetConverter`，覆盖非空、可空属性及映射查询和批量更新参数；保留 null |

时间转换使用 `ToUniversalTime()`，保持同一个时间点。现有字段仍映射为 `timestamp with time zone`，无需数据库迁移，也无需修改数据库或服务器时区。

## 验证结果

| 检查 | 结果 |
| --- | --- |
| Release 解决方案构建 | 通过；0 错误，10 个已有 XML 注释警告 |
| Domain 测试 | 77 通过 |
| Workbench API 测试 | 66 通过 |
| Host 测试 | 164 通过，含 5 个模型/查询转换用例和 10 个真实 PostgreSQL 时间回归用例 |
| 前端测试 | 32 通过 |
| 管理前端生产构建 | 通过；存在已有的模块拆分和大体积产物提示 |
| 生产建表脚本 | 在全新 PostgreSQL 16 临时容器执行成功 |

.NET 合计 307 个用例通过。最终 Release 回归在进程时区及数据库会话时区均为 `Asia/Shanghai` 的环境下执行，真实数据库用例同时覆盖 `+08:00` 和 `-05:00` 输入。

真实数据库验证包含密钥创建、编辑、清除有效期、直接 EF 属性写入、到期查询、登录锁定原生 SQL、锁定中重复请求、TOTP 一次性确认和 counter 防重放、Passkey 凭据持久化与认证更新/禁用、挑战一次性消费、会话撤销、设备轮询乐观比较、授权码一次性消费、刷新令牌轮换和黑名单到期边界。测试使用当前 `production-init.sql`，每个用例在独立事务中回滚。未执行真实认证器或浏览器端到端测试。

本次排除了 `Category=Container` 的已有部署容器测试，以及 `Configured_live_qq_smtp_can_send_test_message` 的真实邮件发送测试。未访问生产数据库或发送外部邮件。

## 复跑

常规回归：

```sh
dotnet build NexusAuth.sln -c Release --no-restore -m:1
Smtp__Password=test-placeholder-only dotnet test NexusAuth.sln -c Release --no-build --no-restore -m:1 --filter 'Category!=Container&FullyQualifiedName!~Configured_live_qq_smtp_can_send_test_message'
node --test tests/frontend/*.test.cjs
```

如果没有设置 `NEXUSAUTH_TEST_POSTGRES_CONNECTION`，真实数据库用例会明确跳过，模型验证和其他常规测试仍会运行。复跑真实数据库用例时，先创建隔离的本机 PostgreSQL 数据库，名称必须以 `_utc_regression` 结尾，再用 `psql` 对该数据库加载仓库的 `production-init.sql`，设置该环境变量并运行：

```sh
dotnet test tests/NexusAuth.Host.IntegrationTests/NexusAuth.Host.IntegrationTests.csproj -m:1 --filter 'Category=PostgreSql'
```

该测试连接仅允许 `localhost` 或 `127.0.0.1`。不要将生产连接字符串用于回归测试。

## 上线应用与配置边界

重新构建并发布 SSO Host 和 Workbench API，两者均使用本次修改的 Domain/Persistence 程序集。只重启旧镜像不会应用修复。此次时间修复无需重新初始化已有数据库。

真实生产环境的证书、邮件和对外 HTTPS 地址没有在本次验证中接入。部署示例 `deploy/direct/sso.env.example` 为本地快速启动而设置 `ASPNETCORE_ENVIRONMENT=Development`；生产部署须按现有快速启动文档的生产切换步骤设置 `Production`，预置受管签名材料及其路径和密码。Host 默认启用 SMTP，因此还须提供有效邮件配置，否则启动校验会拒绝缺少的 `Smtp:Password`。
