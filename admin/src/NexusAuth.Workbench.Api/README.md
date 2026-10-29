# NexusAuth Workbench API

这里是 Workbench 服务端 API。它使用 ASP.NET Core Cookie 保存本地 BFF 会话，通过标准 OpenID Connect 登录 NexusAuth.Host，并提供 Dashboard 使用的管理接口和 Swagger。

完整部署与操作见 [Workbench 使用手册](../../../document/Workbench使用手册.md)，认证注册与复用边界见 [Workbench 认证注册说明](../../../document/Workbench认证注册说明.md)，Cookie + OIDC 接入流程见 [Workbench API 统一登录接入模板](../../../document/Workbench-API统一登录接入模板.md)。

## 目录导览

```text
NexusAuth.Workbench.Api/
├── Program.cs
├── WorkbenchApiModule.cs
├── GlobalUsings.cs
├── NexusAuth.Workbench.Api.csproj
├── appsettings.json
├── appsettings.Development.json
├── Authentication/
│   ├── WorkbenchAuthenticationDefaults.cs
│   ├── WorkbenchAuthenticationExtensions.cs
│   ├── WorkbenchPrincipalFactory.cs
│   ├── Events/
│   │   ├── WorkbenchCookieAuthenticationEvents.cs
│   │   └── WorkbenchOpenIdConnectEvents.cs
│   └── Tokens/
│       ├── IWorkbenchTokenClient.cs
│       ├── WorkbenchTokenClient.cs
│       └── WorkbenchTokenRefreshCoordinator.cs
├── Bootstrap/
│   └── WorkbenchClientCredentialHostedService.cs
├── Options/
│   ├── WorkbenchAuthenticationOptions.cs
│   ├── WorkbenchAuthenticationOptionsValidator.cs
│   ├── WorkbenchSecurityOptions.cs
│   ├── WorkbenchSwaggerOptions.cs
│   └── WorkbenchBootstrapOptions.cs
├── Authorization/
├── Controllers/
├── Filters/
└── Properties/
```

| 目录 | 职责 |
| --- | --- |
| [`Authentication`](./Authentication) | Cookie/OIDC 方案组装、Principal 转换、认证事件和 BFF 令牌操作 |
| [`Authentication/Events`](./Authentication/Events) | Cookie 验证、令牌刷新与 OIDC 登录/登出事件 |
| [`Authentication/Tokens`](./Authentication/Tokens) | refresh、introspection、revoke 的 DTO、异常、客户端和并发刷新协调 |
| [`Bootstrap`](./Bootstrap) | 启动时幂等登记 Workbench 自己的 OAuth 客户端与 API resource |
| [`Options`](./Options) | `IOptions<T>` 配置模型和认证配置校验 |
| [`Authorization`](./Authorization) | 默认访问策略、requirement 和授权 handler |
| [`Controllers`](./Controllers) | 登录生命周期与管理 API |
| [`Filters`](./Filters) | 管理 API 统一结果和错误边界 |

根目录只保留应用启动、模块组装、全局 using、项目文件和 appsettings。`Properties/launchSettings.json` 只提供本地 Development 环境与监听地址。

## 配置入口

业务配置统一来自 `appsettings.json` 和 `appsettings.{Environment}.json`，代码通过 `IOptions<T>` 读取。基础文件包含连接串、认证、流程状态 Redis、Security、Swagger、Bootstrap 和 Smtp 的完整结构，并提供 Bootstrap 默认元数据；Development 文件覆盖本地数据库、`http://localhost:5100`、`http://localhost:5273/signin-oidc`、`http://localhost:5273/` 和 `SameAsRequest` Cookie 策略。

`Auth:ClientSecret` 在模板和 Development 文件中留空，运行前需要在私有配置中填写。根 Compose 通过 `NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE` 选择唯一挂载文件；生产建议使用 gitignore 的 `docker/workbench.private.appsettings.json`，独立部署使用 `deploy/direct/workbench.appsettings.json`。不要提交运行密钥。

项目不再提供 `NEXUSAUTH_WORKBENCH_*` 单下划线配置别名。ASP.NET Core 原生环境变量和命令行覆盖仍然可用，但当前部署流程以环境 JSON 为主。

## 本地运行

先填写 `appsettings.Development.json` 中的 `Auth:ClientSecret`，再从仓库根目录执行：

```bash
dotnet run --project admin/src/NexusAuth.Workbench.Api
```

默认地址是 `http://localhost:5051`，Swagger 位于 `http://localhost:5051/swagger`。Dashboard 的 Vite 代理需要把 `/api` 和 `/signin-oidc` 转发到该地址。
