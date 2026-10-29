# NexusAuth.Extension

`NexusAuth.Extension` 是通用、轻量的 OIDC HTTP 协议适配层，不包含 ASP.NET Core 登录中间件、Cookie、前端路由或 Workbench 流程状态。它不依赖 `NexusAuth.Shared`、`Luck` 或 ASP.NET Core JWT Bearer，也未引入 `IdentityModel` / `Duende.IdentityModel` 客户端包。

```csharp
using NexusAuth.Extension;

services.AddNexusAuth(options =>
{
    options.Authority = "https://identity.example.com";
    options.ClientId = "my-api";
    options.ClientSecret = configuration["Auth:ClientSecret"];
    options.RequireHttpsMetadata = true;
    options.ClientAuthenticationMethod = ClientAuthenticationMethod.ClientSecretBasic;
});
```

`AddNexusAuth` 返回 `IServiceCollection`。`NexusAuthClientOptions` 只包含：

| 配置 | 说明 |
| --- | --- |
| `Authority` | OIDC Provider 基础地址 |
| `ClientId` | 客户端 ID |
| `ClientSecret` | 客户端密钥；`None` 方式可留空 |
| `RequireHttpsMetadata` | 是否要求发现和端点使用 HTTPS |
| `ClientAuthenticationMethod` | `ClientSecretBasic`（默认）、`ClientSecretPost` 或 `None` |

`INexusAuthClient` 以 Scoped 注册，提供：

- `ExchangeCodeAsync(code, verifier, redirectUri, ct)`
- `RefreshTokensAsync(refreshToken, ct)`
- `IntrospectAccessTokenAsync(accessToken, ct)`
- `RevokeTokenAsync(token, tokenTypeHint, ct)`
- `FetchConfigurationAsync(ct)`

发现文档和 JWKS 由单例 `IConfigurationManager<OpenIdConnectConfiguration>` 管理。调用方负责登录端点、Cookie、会话、流程状态和业务授权。

这里使用的是微软 `Microsoft.IdentityModel.*` 元数据组件，与 `IdentityModel` / `Duende.IdentityModel` 客户端包不同。Web 登录使用 ASP.NET Core `AddOpenIdConnect` 与 Cookie；API 的 Bearer 认证使用 `AddJwtBearer`。

旧版接入需要将 `IOidcWorkbenchService` 替换为 `INexusAuthClient`，并将浏览器登录、PKCE 和 ID Token 校验交给标准 OIDC 中间件。`AddNexusAuth` 不再返回存储 builder；`IFlowStateStore` 及其内存、Redis 实现已迁入 Workbench，通过 `AddWorkbenchFlowState().UseInMemory()` / `.UseRedis(...)` 选择，不再发布独立的 `NexusAuth.Extension.Redis` 项目。
