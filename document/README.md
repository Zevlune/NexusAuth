# 使用文档

本目录按应用用途组织。**NexusAuth.Host 是独立身份认证服务；Workbench API 和 Dashboard 合起来是一套管理工作台。**

## 两套使用手册

| 手册 | 适用对象 | 主要内容 |
| --- | --- | --- |
| [NexusAuth.Host 使用手册](NexusAuth.Host使用手册.md) | 部署认证服务、接入业务系统、使用登录与账号安全功能的人 | Host 独立启动、数据库和签名配置、登录与邮箱、TOTP / Passkey、OAuth/OIDC、SCIM / Open API、排错 |
| [Workbench 使用手册](Workbench使用手册.md) | 部署或使用管理后台的人 | Workbench API + Dashboard 联合部署、BFF 登录、用户 / 客户端 / API 资源管理、审计、管理权限、网关边界、排错 |

Host 的启动流程不要求启动 Workbench 或 Dashboard。Workbench 手册会说明其对 Host 和共享数据库的依赖，但不将 Host 的协议服务当作管理工作台的一部分。

## 专题文档

| 文档 | 范围 |
| --- | --- |
| [微信小程序接入](微信小程序接入.md) | `wx.login` 一次性 code、公共客户端、Host 配置、令牌刷新和 UserInfo |
| [OAuth/OIDC 协议设计](11-OAuth-OIDC协议设计.md) | Host 的端点、参数校验、授权和令牌规则 |
| [SMTP 配置与发信验证](SMTP发信试验.md) | Host 邮件配置、邮箱业务流程、发信排错与测试 |
| [Workbench API 统一登录接入模板](Workbench-API统一登录接入模板.md) | 复用 .NET 接入扩展与 BFF 登录流程 |
| [Dashboard 统一登录接入模板](Workbench-Dashboard统一登录接入模板.md) | 前端登录路由、同源代理和回调处理 |
| [APISIX 网关对接手册](13-APISIX网关对接手册.md) | 外置网关认证模式与信任边界 |
| [UTC 时间戳发布检查](utc-timestamp-release-check.md) | 持久化与 API 时间格式的发布检查 |

[快速启动旧入口](01-快速启动.md) 已改为两套手册的导航。专题文档用于补充主手册；部署时先按所属应用的主手册完成配置。

[返回仓库首页](../README.md) · [English guide](../docs/en/user-guide.md)
