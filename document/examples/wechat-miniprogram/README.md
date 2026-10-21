# NexusAuth 微信小程序最小登录示例

这是一个无需安装依赖或执行构建的原生微信小程序示例。小程序直接请求 NexusAuth.Host，不经过 BFF。

## 运行

1. 在微信开发者工具中选择“导入项目”，目录指向 `document/examples/wechat-miniprogram/`。
2. 将 `project.config.json` 中的 `appid` 从 `touristappid` 替换为实际小程序 AppID。
3. 按注释修改 `config.js`：至少替换 `API_BASE`，并确认 `CLIENT_ID`、`SCOPE` 与 Host 中的客户端记录一致。
4. 在微信小程序后台把 Host 的 HTTPS origin 加入 `request` 合法域名，例如 `https://sso.example.com`。

## Host 先决条件

- 在 Host 服务端启用微信小程序登录，并配置小程序 AppID 与 AppSecret；AppSecret 只保存在 Host 服务端。
- 在 NexusAuth 中注册公共客户端，`token_endpoint_auth_method` 为 `none`，允许微信小程序扩展 grant、`refresh_token` 和 `openid profile offline_access`，且不要创建客户端 secret。
- 确认 `/connect/token` 与 `/connect/userinfo` 可通过公网 HTTPS 访问，反向代理保留 `/connect/*` 路径。

详细服务端配置见 [微信小程序接入](../../微信小程序接入.md)。

## 示例行为

- 登录时每次调用新的 `wx.login` 获取一次性 `code`；请求失败不会用旧 `code` 自动重试。
- Token 请求只发送 `client_id`、grant/scope 参数和必要的 `code`/`refresh_token`，不发送 secret 或 Authorization。
- access token 在到期前 60 秒自动刷新；没有 refresh token 时重新登录。refresh token 轮换后用响应中的新值覆盖本地值。
- 并发登录和并发刷新分别共享同一个进行中的操作。网络错误和 5xx 不会删除会话，也不会无限重试。
- `/connect/userinfo` 返回 401 时最多恢复并重试一次。页面只显示 `sub` 和 `name`，不显示 token。
- Token 保存在 `wx` 小程序本地存储中；这只是业务本地存储，不是经认证的安全存储。示例不打印 token、微信 `code` 或服务端响应。

“清除本机会话”只删除当前设备上的小程序存储，不会全局撤销 NexusAuth 会话或 refresh token。

`wx.login` 只提供微信身份登录凭证，不会返回头像或昵称；页面展示的 `name` 来自 NexusAuth UserInfo。
