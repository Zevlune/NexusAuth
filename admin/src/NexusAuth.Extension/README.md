# NexusAuth.Extension

`NexusAuth.Extension` 是配合 ASP.NET Core 标准 OpenID Connect 使用的流程状态扩展。它保存登录往返期间待消费的临时状态，并自行签发 HttpOnly 的浏览器绑定 Cookie；它不管理登录会话 Cookie，也不承担 OAuth/OIDC 协议客户端、登录端点、令牌兑换或身份校验职责。

旧版 `AddNexusAuth`、`INexusAuthClient` 及其协议模型已经移除。项目不再依赖 `NexusAuth.Shared` 或 `NexusAuth.Workbench`，也不会注册登录端点、生成 PKCE、兑换授权码或重复校验 ID Token；这些工作全部由应用的 `AddOpenIdConnect` 和 Cookie 认证完成。

## 注册

扩展与标准 OIDC 中间件组合使用。应用仍需自行调用 `AddAuthentication().AddOpenIdConnect(...)`，并负责授权、回调和 Cookie 策略。

```csharp
using NexusAuth.Extension;

services.AddOpenIdConnectFlowState()
    .UseConfiguration(configuration.GetSection("Auth:FlowState"));
```

`UseConfiguration` 只绑定传入的配置节。默认使用进程内存储：

```json
{
  "Auth": {
    "FlowState": {
      "Provider": "InMemory",
      "Lifetime": "00:10:00",
      "Capacity": 10000
    }
  }
}
```

多实例部署将 `Provider` 设为 `Redis`，让回调可以落到任意实例：

```json
{
  "Auth": {
    "FlowState": {
      "Provider": "Redis",
      "Redis": {
        "Configuration": "localhost:6379",
        "KeyPrefix": "NexusAuth:oidc:flow",
        "Database": -1
      }
    }
  }
}
```

`Provider` 区分大小写，只接受 `InMemory` 和 `Redis`。容量、TTL、Provider 与 Redis 配置会在应用启动时校验；未选择 Redis 时不会创建 Redis 连接。已经注册 `IConnectionMultiplexer` 时，Redis 存储会复用该连接，`Redis.Configuration` 可以留空。

`UseInMemory()`、`UseRedis(...)` 和 `UseFlowStateStore<T>()` 仍可用于代码注册。自定义 `IFlowStateStore` 默认注册为 Scoped，也可以显式选择 Singleton。`UseRedis(...)` 提供跨实例共享、原子一次消费和共享容量，默认 KeyPrefix 为 `NexusAuth:oidc:flow`。

`OpenIdConnectFlowStateOptions` 提供 `Provider`、`Lifetime` 和 `Capacity`；`OpenIdConnectFlowState` 是组合标准 OIDC 流程状态的服务入口。默认 TTL 为 10 分钟，默认容量为 10,000 个待完成流程。应用应将 `Lifetime` 与 `AddOpenIdConnect` 的远程认证超时保持一致，并在容量或请求限流触发时返回明确的 `429`。

## 与 AddOpenIdConnect 接线

注册扩展不会自动拦截 OIDC challenge 或回调。应用必须在标准 OIDC 事件中调用 `ValidateAsync`，并在发起 challenge 前调用 `BeginAsync`。

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using NexusAuth.Extension;

services.AddAuthentication()
    .AddOpenIdConnect("oidc", options =>
    {
        // 配置 Authority、ClientId、ClientSecret、Scope、CallbackPath 等标准选项。
        options.Events.OnMessageReceived = context => context.HttpContext.RequestServices
            .GetRequiredService<OpenIdConnectFlowState>()
            .ValidateAsync(context);
    });
```

发起登录时先登记流程，再 challenge。`BeginAsync` 会把流程标识写入 `AuthenticationProperties`，并发出本次流程专用的浏览器绑定 Cookie：

```csharp
app.MapGet("/auth/login", async (HttpContext httpContext, CancellationToken ct) =>
{
    var properties = new AuthenticationProperties
    {
        RedirectUri = "/auth/callback",
        IsPersistent = true,
    };
    var flows = httpContext.RequestServices.GetRequiredService<OpenIdConnectFlowState>();

    try
    {
        await flows.BeginAsync(httpContext, properties, "oidc", ct);
        await httpContext.ChallengeAsync("oidc", properties);
    }
    catch
    {
        await flows.CancelAsync(httpContext, properties, "oidc", CancellationToken.None);
        throw;
    }
});
```

`ValidateAsync` 会原子消费待处理流程，核对浏览器绑定并清理绑定 Cookie；缺失、过期、重放或绑定不匹配时会让标准 OIDC 回调失败。challenge 或回调前处理失败时调用 `CancelAsync`，同时移除服务端流程和浏览器绑定 Cookie。

迁移后使用 `NexusAuth.Extension` 命名空间和 `AddOpenIdConnectFlowState()` 注册入口。Redis 默认前缀从 `NexusAuth:workbench:flow` 改为 `NexusAuth:oidc:flow`，浏览器绑定 Cookie 名称也已调整；升级前尚未完成的登录需要重新发起。

多实例还必须共享并持久化 ASP.NET Core Data Protection key ring。Redis 只共享流程状态；OIDC correlation cookie、nonce cookie 和会话 Cookie 仍依赖相同的 key ring 才能跨实例解密。
