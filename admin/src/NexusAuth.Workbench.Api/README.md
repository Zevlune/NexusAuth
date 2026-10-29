# NexusAuth Workbench API

这里是 Workbench 服务端 API。它同时支持 ASP.NET Core Cookie BFF 会话和 JWT Bearer，通过标准 OpenID Connect 登录 NexusAuth.Host，并提供 Dashboard、无头 API 客户端使用的管理接口和 Swagger。默认认证方案是 `Workbench` 策略方案：请求没有 `Authorization` 头时使用 Cookie；出现任意 `Authorization` 头时只使用 Bearer，不会回退到 Cookie。

完整部署与操作见 [Workbench 使用手册](../../../document/Workbench使用手册.md)，认证注册与复用边界见 [Workbench 认证注册说明](../../../document/Workbench认证注册说明.md)，Cookie + Bearer + OIDC 接入流程见 [Workbench API 统一登录接入模板](../../../document/Workbench-API统一登录接入模板.md)。

## 目录导览

```text
NexusAuth.Workbench.Api/
├── Authentication/
│   ├── Events/
│   │   ├── WorkbenchCookieAuthenticationEvents.cs
│   │   ├── WorkbenchJwtBearerEvents.cs
│   │   └── WorkbenchOpenIdConnectEvents.cs
│   ├── Tokens/
│   │   ├── IWorkbenchTokenClient.cs
│   │   ├── IWorkbenchTokenRefreshCoordinator.cs
│   │   ├── WorkbenchIntrospectionResult.cs
│   │   ├── WorkbenchProtocolException.cs
│   │   ├── WorkbenchProtocolResponse.cs
│   │   ├── WorkbenchTokenClient.cs
│   │   ├── WorkbenchTokenRefreshCoordinator.cs
│   │   └── WorkbenchTokenResult.cs
│   ├── WorkbenchAuthenticationDefaults.cs
│   ├── WorkbenchAuthenticationExtensions.cs
│   └── WorkbenchPrincipalFactory.cs
├── Authorization/
│   ├── WorkbenchAccessAuthorizationHandler.cs
│   ├── WorkbenchAccessRequirement.cs
│   ├── WorkbenchAuthorizationPolicies.cs
│   └── WorkbenchAuthorizationServiceCollectionExtensions.cs
├── Bootstrap/
│   └── WorkbenchClientCredentialHostedService.cs
├── Controllers/
│   ├── ApiResourcesController.cs
│   ├── AuthController.cs
│   ├── ClientMetadataController.cs
│   ├── ClientsController.cs
│   ├── CurrentIdentityController.cs
│   ├── LoginAuditsController.cs
│   ├── OpenApiCredentialsController.cs
│   ├── ScimCredentialsController.cs
│   ├── UserPasskeysController.cs
│   └── UsersController.cs
├── Filters/
│   ├── WorkbenchApiResultAttribute.cs
│   └── WorkbenchApiResultFilter.cs
├── Models/
│   └── Requests/
│       ├── CreateOpenApiCredentialRequest.cs
│       ├── CreateScimCredentialRequest.cs
│       ├── RevokePasskeyRequest.cs
│       ├── UpdateManagedUserStatusRequest.cs
│       ├── UpdateOpenApiCredentialRequest.cs
│       └── UpdateScimCredentialRequest.cs
├── Options/
│   ├── WorkbenchAuthenticationOptions.cs
│   ├── WorkbenchAuthenticationOptionsValidator.cs
│   ├── WorkbenchBootstrapOptions.cs
│   ├── WorkbenchSecurityOptions.cs
│   └── WorkbenchSwaggerOptions.cs
├── Properties/
│   └── launchSettings.json
├── Dockerfile
├── GlobalUsings.cs
├── NexusAuth.Workbench.Api.csproj
├── Program.cs
├── README.md
├── WorkbenchApiModule.cs
├── appsettings.Development.json
└── appsettings.json
```

| 目录 | 职责 |
| --- | --- |
| [`Authentication`](./Authentication) | Cookie、OIDC、JWT Bearer 方案组装、默认策略选择、Principal 转换和 BFF 令牌操作 |
| [`Authentication/Events`](./Authentication/Events) | Cookie 刷新/内省、JWT 信任与撤销检查、OIDC 流程和前端登录/错误契约 |
| [`Authentication/Tokens`](./Authentication/Tokens) | refresh、introspection、revoke 的模型、响应/异常、客户端和并发刷新协调 |
| [`Bootstrap`](./Bootstrap) | 启动时幂等登记 Workbench 自己的 OAuth 客户端与 API resource |
| [`Options`](./Options) | `IOptions<T>` 配置模型和认证配置校验 |
| [`Authorization`](./Authorization) | 默认访问策略、requirement 和授权 handler |
| [`Controllers`](./Controllers) | 登录生命周期与管理 API |
| [`Filters`](./Filters) | 管理 API 统一结果和错误边界 |
| [`Models/Requests`](./Models/Requests) | 控制器请求模型，每类型一文件 |

根目录只保留应用启动、模块组装、全局 using、项目文件和 appsettings。`Properties/launchSettings.json` 只提供本地 Development 环境与监听地址。

## 配置入口

业务配置统一来自 `appsettings.json` 和 `appsettings.{Environment}.json`，代码通过 `IOptions<T>` 读取。基础文件包含连接串、认证、流程状态 Redis、Security、Swagger、Bootstrap 和 Smtp 的完整结构，并提供 Bootstrap 默认元数据；Development 文件覆盖本地数据库、`http://localhost:5100`、`http://localhost:5273/signin-oidc`、`http://localhost:5273/` 和 `SameAsRequest` Cookie 策略。

`Auth:ClientSecret` 在模板和 Development 文件中留空，运行前需要在私有配置中填写。根 Compose 通过 `NEXUSAUTH_WORKBENCH_APPSETTINGS_FILE` 选择唯一挂载文件；生产建议使用 gitignore 的 `docker/workbench.private.appsettings.json`，独立部署使用 `deploy/direct/workbench.appsettings.json`。不要提交运行密钥。

`Auth:AllowedBearerClientIds` 是额外允许调用 Workbench API 的客户端 ID 数组，默认空。Bearer access token 的 `client_id` 始终允许等于 `Auth:ClientId`；需要让其他已登记客户端调用时再逐项加入该数组。

项目不再提供 `NEXUSAUTH_WORKBENCH_*` 单下划线配置别名。ASP.NET Core 原生环境变量和命令行覆盖仍然可用，但当前部署流程以环境 JSON 为主。

## 本地运行

先填写 `appsettings.Development.json` 中的 `Auth:ClientSecret`，再从仓库根目录执行：

```bash
dotnet run --project admin/src/NexusAuth.Workbench.Api
```

默认地址是 `http://localhost:5051`，Swagger 位于 `http://localhost:5051/swagger`。Dashboard 的 Vite 代理需要把 `/api` 和 `/signin-oidc` 转发到该地址。

## 请求认证

Dashboard 仍通过 `.NexusAuth.Workbench` Cookie 调用 `/api`，浏览器 JavaScript 不读取、保存或直接发送 access token。无头 API 客户端不依赖 Cookie，直接发送 Bearer：

```bash
curl -H "Authorization: Bearer $NEXUSAUTH_ACCESS_TOKEN" \
  http://localhost:5051/api/auth/me
```

Bearer 使用 `Auth:Authority` 作为 issuer、`Auth:Audience` 作为 audience，只接受 RS256 签名且 `token_use=access_token` 的 access token。`client_id` 等于 `Auth:ClientId` 或出现在 `Auth:AllowedBearerClientIds` 后，再由 `IUserTokenValidator` 检查用户禁用、会话撤销、token epoch 和 jti 黑名单。JWT Bearer 与 OIDC 登录共用标准框架的 metadata manager，因此 Discovery 和 JWKS 使用同一份缓存。

OIDC ID token 的 audience 是 `Auth:ClientId`，不能作为 API Bearer token。请求只要出现任意 `Authorization` 头就选择 Bearer；头为空、方案不是 Bearer 或 token 无效时返回 401，即使同一请求带有有效 Cookie 也不回退。受保护 API 由框架附带 `WWW-Authenticate: Bearer`；`/api/auth/me` 因 `AllowAnonymous` 自行返回 401，不附带该 challenge。

`POST /api/auth/logout` 的语义随入口区分：Bearer 调用请求 Provider revoke 本次保存的 access token，不清 Cookie、不发起 Provider 登出并返回空 `logoutUrl`。revoke 失败只记录日志，接口仍返回 200，成功响应不保证远端已完成撤销。Cookie 调用保持原行为：best-effort 请求撤销保存的 refresh/access token，按配置执行 Provider 登出并清除本地 Cookie。
