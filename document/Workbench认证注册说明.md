# Workbench 认证注册与复用边界

`AddWorkbenchAuthentication` 是本应用的组装入口。它依次绑定配置、注册 BFF 会话服务、连接 Cookie/OIDC 中间件，以及启用可选的启动预热。具体协议行为仍配置在标准 `OpenIdConnectOptions` 上，没有另外一套 SDK 认证参数。

## 哪些代码可以复用

| 能力 | 放置位置 | 原因与入口 |
| --- | --- | --- |
| 浏览器绑定、登录流程一次性消费、TTL、容量、内存/Redis 存储 | Extension | 已有 `AddOpenIdConnectFlowState()`；应用按自己的登录入口和事件接线 |
| 客户端 Basic 认证适配 | Extension | `IHttpClientBuilder.UseOpenIdConnectClientSecretBasic(scheme)`；只读取指定方案的原生 Options，不认识 Workbench 配置 |
| 原生元数据启动预热 | Extension | `AddOpenIdConnectMetadataWarmup()`；支持多个 OIDC 方案，各自使用原生配置管理器，不创建新缓存 |
| 发现文档端点地址检查 | Extension | `OpenIdConnectEndpointValidator.Validate(...)`；后台请求与浏览器跳转共用 HTTPS/URI 检查 |
| Cookie 名称、登录路径、24 小时滑动会话 | Workbench | 属于当前应用的会话策略，其他客户端不一定使用 Cookie 或同一有效期 |
| 回调地址、业务 scope、严格 RS256、claim 到 Principal 的转换 | Workbench | 属于信任和业务策略，不适合作为通用扩展的默认限制 |
| 刷新、内省、撤销的 HTTP 客户端 | Workbench，暂保留 | HTTP 操作本身具备复用性，但当前只由 Workbench 调用，接口绑定其认证方案、响应与异常模型；本次不新增一套通用令牌 SDK API |
| Cookie 事件、OIDC 事件、刷新协调器 | Workbench | 负责本应用票据续期、管理员撤销后的拒绝规则、并发刷新结果复用和 JSON 登录/登出契约，需要由应用选择策略 |

Extension 不依赖 `NexusAuth.Shared`、Workbench 或 Luck，也没有引入 `IdentityModel` / `Duende.IdentityModel` 客户端库。它直接扩展微软的认证和 HttpClient 注册方式。

## 注册顺序与生命周期

`AddAuthenticationOptions` 绑定 `Auth` 和流程存储配置，通过 Options 规范化并启动校验。不会把 `WorkbenchAuthenticationOptions` 对象直接作为单例注册。

`AddSessionServices` 注册请求范围内的 Cookie/OIDC 事件和令牌客户端。刷新协调器通过接口注册为单例，以便合并来自不同请求的同一 refresh token 刷新；实际令牌请求通过它创建的 scope 执行。Basic handler 由 HttpClientFactory 创建和回收，不需要应用再注册实现类或手工释放共享实例。

`AddAuthenticationHandlers` 连接本应用 Cookie 和标准 OIDC 方案。对 `OpenIdConnectOptions` 的配置依赖 `IOptions<T>` 和 `IHttpClientFactory`，没有提前构造 ServiceProvider。Basic 适配器到发送请求时才读取原生 Options，避免在构建 Backchannel 时递归构造同一认证方案。

`AddOpenIdConnectMetadataWarmup` 是可选优化。框架本身已经负责发现文档和签名密钥缓存；预热只提前触发读取，成功后不再额外轮询。各方案失败后独立重试，应用关闭可取消等待。

## OpenIdConnectOptions 各项用途

| 配置 | Workbench 的用途 |
| --- | --- |
| `SignInScheme` / `SignOutScheme` | 成功登录写入本应用 Cookie；Provider 登出时从同一票据读取 ID Token |
| `Authority` | Provider 的身份与发现入口；后续端点、元数据及 JWKS 缓存交给框架 |
| `ClientId` / `ClientSecret` | 本应用在 Provider 的客户端凭据；Basic 适配器也从这份原生 Options 读取 |
| `RequireHttpsMetadata` | 默认要求发现和后台端点使用 HTTPS；本地 HTTP 调试需显式放开 |
| `CallbackPath` | 中间件拦截回调的本地路径；完整 RedirectUri 由 Workbench OIDC 事件设置 |
| `ResponseType = code` / `UsePkce = true` | 使用授权码与 PKCE；verifier、授权码兑换、ID Token 协议校验由框架执行 |
| `ResponseMode = query` | Provider 通过顶层 GET 返回当前回调，匹配 Lax Cookie 策略 |
| `SaveTokens` | 将令牌保存在受保护的认证票据，供 BFF 刷新、内省与撤销 |
| `MapInboundClaims = false` | 保留 OIDC 原始 claim 名称，应用再统一转换 Principal |
| `DisableTelemetry` | 不向 Provider 附带组件版本遥测参数 |
| `PushedAuthorizationBehavior = Disable` | 保留普通授权请求方式，本次不启用可选的 PAR；不是所有 OIDC 客户端都必须禁用 PAR |
| `Scope.Clear()` 和添加 scope | 使用本应用校验后的 scope；规范化时补入业务 Audience，避免重复 scope |
| `ValidIssuer` | ID Token 签发者必须匹配配置的 Authority |
| `ValidAudience` | ID Token 的受众是当前 ClientId；不是 access token 的业务 API Audience |
| `ValidateIssuerSigningKey` / `ValidAlgorithms` | 校验签名密钥并限制 RS256；事件还保留当前应用要求 ID Token 必须带签名的规则 |
| `ClockSkew` | 允许 30 秒服务器时钟偏差 |
| `NameClaimType` | 使用 `name` 作为认证身份的名称字段 |
| `Backchannel` | 后台发现、JWKS、授权码兑换共用受控客户端与超时；Basic 只用于 POST 表单 |
| `CorrelationCookie` / `NonceCookie` | 框架分别用于登录往返关联和 ID Token 重放防护；当前 GET 回调采用 Lax，并遵从本应用 Secure 策略 |
| `RemoteAuthenticationTimeout` | 与额外流程存储的 Lifetime 保持一致，避免两层登录状态有效期不一致 |

部分赋值显式重申了框架默认值，例如 PKCE 和 HttpOnly。这是便于审阅当前安全策略，不代表应用重新实现了对应协议。代码中的中文注释与本表相对应。

复用接线示例见 [Extension README](../admin/src/NexusAuth.Extension/README.md)。
