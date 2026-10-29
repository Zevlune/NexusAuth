# NexusAuth

NexusAuth 包含身份认证服务和管理工作台两套用途不同的应用。请按部署或使用目标选择手册。

| 应用 | 组成 | 用途 | 使用文档 |
| --- | --- | --- | --- |
| **NexusAuth.Host** | `src/NexusAuth.Host` | 独立运行的 OAuth/OIDC 身份认证服务器；承载登录、账号安全、令牌签发及身份接口 | [Host 使用手册](document/NexusAuth.Host使用手册.md) |
| **Workbench + Dashboard** | `admin/src/NexusAuth.Workbench.Api` + `admin/src/NexusAuth.Workbench.Dashboard` | 一套管理工作台；API 提供管理与 BFF 登录能力，Dashboard 提供操作界面 | [Workbench 联合使用手册](document/Workbench使用手册.md) |

Host 可以单独部署。Workbench 依赖可用的身份认证服务，当前实现与 Host 共用 NexusAuth 数据库；Dashboard 通过 Workbench API 操作管理数据。Dashboard 不能单独承担认证服务的职责。

Host 的应用配置从 `appsettings.json` 和 `appsettings.{环境}.json` 读取。SCIM 用户同步默认开启，无需同步的部署可设置 `Scim:Enabled` 为 `false`；关闭后 SCIM 接口不会注册，详见 [SCIM 接口说明](document/NexusAuth.Host使用手册.md#7-scim-20-用户供给)。

## 从哪里开始

- **提供统一登录、接入业务系统或调用身份协议**：阅读 [Host 使用手册](document/NexusAuth.Host使用手册.md)，按“独立启动 → 配置 → 协议接入”的顺序操作。
- **管理用户、客户端、API 资源或审计数据**：阅读 [Workbench 使用手册](document/Workbench使用手册.md)，将 Workbench API 和 Dashboard 作为一套启动和配置。
- **阅读协议细节**：查看 [OAuth/OIDC 协议设计](document/11-OAuth-OIDC协议设计.md)。
- **接入 APISIX**：查看 [APISIX 网关对接手册](document/13-APISIX网关对接手册.md)，先核对网关模式与默认 BFF 模式的区别。

## 安全默认值与本地运行

- Workbench 基础配置中的 `Auth:Authority` 为空，`Auth:RequireHttpsMetadata=true`；仓库的 Development 配置为本机开发显式覆盖为 `http://localhost:5100` 和 `false`。配置层允许任何环境显式设为 `false`，但规范上仅限隔离本地开发显式关闭。
- Cookie 的基础 `Security:CookieSecurePolicy=Always`；Development 显式覆盖为 `SameAsRequest`。部署时不要在 HTTP 环境静默降低生产默认值。
- Swagger 仅在 Development 环境或显式设置 `Swagger:Enabled=true` 时启用。
- 转发头默认关闭。启用时必须配置明确的 `Security:KnownProxies` 或 `Security:KnownNetworks`；应用只信任这些代理或网络。
- `/api` 与 `/signin-oidc` 响应使用 `Cache-Control: no-store`，避免登录状态和令牌交换结果进入浏览器或代理缓存。

本地 Compose 必须显式叠加本地覆盖文件，由它启用隔离演示所需的 HTTP 设置：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

基础 `docker-compose.yml` 保持 HTTPS 安全默认，不因本地预览而降低；生产使用 HTTPS Authority、`RequireHttpsMetadata=true`、`CookieSecurePolicy=Always` 和明确可信的代理网络。Compose 中 `admin-api` 没有映射宿主机端口。Host 的 SMTP 默认关闭，可在 appsettings 中启用并补齐邮件配置。

## 代码结构

```text
src/
  NexusAuth.Host/                   身份认证服务器
  NexusAuth.Application/            应用服务
  NexusAuth.Domain/                 领域模型
  NexusAuth.Persistence/            PostgreSQL / EF Core 持久层
  NexusAuth.Shared/                 共享配置、邮件等基础能力
admin/src/
  NexusAuth.Workbench.Api/          管理工作台 API 与 BFF
  NexusAuth.Workbench.Dashboard/    管理工作台界面
  NexusAuth.Extension/              .NET 接入扩展

demo/                              各类业务接入示例
tests/                             自动化测试
document/                          两套使用手册与专题文档
production-init.sql                数据库初始化脚本
docker-compose.yml                 本地容器编排
```

## 文档范围

两套手册依据当前仓库的实现重写，包含部署、配置、操作步骤和排错。协议行为以 Host 的端点实现为准，管理操作以 Workbench 控制器和 Dashboard 页面为准。旧的 [快速启动入口](document/01-快速启动.md) 保留为导航，避免继续混用两套应用的启动步骤。

完整索引见 [document/README.md](document/README.md)。英文入口见 [English guide](docs/en/user-guide.md)。
