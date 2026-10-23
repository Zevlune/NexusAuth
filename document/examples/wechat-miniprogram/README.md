# NexusAuth 微信小程序最小登录示例

这是一个无需安装依赖或执行构建的原生微信小程序示例。小程序直接请求 NexusAuth.Host，不经过 BFF。

## 本地调试

1. 确认 Host 已在本机 `http://localhost:5100` 启动，数据库已按项目要求初始化。示例的 `config.js` 已将 `API_BASE` 指向该地址。
2. 在微信开发者工具中选择“导入项目”，目录使用本目录 `document/examples/wechat-miniprogram/`。项目配置文件为 `project.config.json`，小程序根目录为 `./`。
3. 项目已配置本次调试使用的小程序 AppID；切换小程序时，应同步修改 `project.config.json` 和 Host 服务端配置。
4. 项目已设置 `urlCheck: false`；在“详情” > “本地设置”中确认已勾选“不校验合法域名、web-view（业务域名）、TLS 版本以及 HTTPS 证书”。此设置仅用于本地调试。
5. 确认 `config.js` 中的 `CLIENT_ID`、`SCOPE` 与 NexusAuth 公共客户端记录一致，然后在开发者工具中重新编译并测试昵称确认与微信登录流程。

微信小程序登录需要服务端调用微信接口换取用户身份，因此 AppSecret 只能配置在 NexusAuth.Host 服务端，不能放入 `config.js`、`project.config.json` 或小程序前端代码。本机服务端配置保存在被 Git 忽略的根目录 `.env.local` 中；启动 Host 时需将其中的变量加载到进程环境。

Host 还应启用微信小程序登录，并注册 `token_endpoint_auth_method` 为 `none` 的公共客户端；该客户端应允许微信小程序扩展 grant、`refresh_token` 以及 `CLIENT_ID`/`SCOPE` 所需的权限，且不得配置客户端 secret。

详细服务端配置见 [微信小程序接入](../../微信小程序接入.md)。

## 生产环境

生产部署必须使用公网可访问的 HTTPS 地址，例如 `https://sso.example.com`：将 `config.js` 的 `API_BASE` 改为生产地址，在微信小程序后台把该 origin 加入 `request` 合法域名，并确保 TLS 版本与 HTTPS 证书有效；如果使用 `web-view`，还需按要求登记业务域名。生产环境不能依赖开发者工具的本地校验绕过设置。

同时确保 `/connect/token` 与 `/connect/userinfo` 可通过 HTTPS 访问，反向代理保留 `/connect/*` 路径。

## 示例行为

- 登录时每次调用新的 `wx.login` 获取一次性 `code`；请求失败不会用旧 `code` 自动重试。
- 主页面“微信登录”按钮只打开应用内昵称确认框，不发起登录请求。取消确认不会调用 `wx.login`。
- 确认框提供选填昵称。只有在用户本次显式提交非空昵称时，微信 grant 的 `/connect/token` 表单才增加 `nickname`；该值去除首尾空格后最多 100 个 Unicode 码点，不会写入本地会话或在刷新、自动重新登录时复用。已有页面用户的昵称会作为确认框默认值；未知昵称显示“点击使用微信昵称”入口，留空则保留 NexusAuth 账号当前昵称。
- 点击底部“确认登录”后，页面只调用一次 `auth.login(nickname)`。昵称草稿非法或微信昵称审核未通过时不会发起请求；修正昵称后可以再次确认。
- 新 Host 的微信 `/connect/token` 成功响应包含 `user: {"sub":"...","name":"..."}`。登录页面直接展示并保存该用户信息，不再额外请求 `/connect/userinfo`；旧 Host 响应缺少有效 `user` 时，页面才回退读取 UserInfo。
- access token 在到期前 60 秒自动刷新；没有 refresh token 时自动重新登录。refresh token 轮换后用响应中的新值覆盖本地值。
- 刷新请求只发送 `grant_type=refresh_token`、`client_id` 和 `refresh_token`，不发送昵称、secret 或 Authorization。刷新响应缺少 `user` 时，本地会话保留上一次有效的 `session.user`。
- 并发登录和并发刷新分别共享同一个进行中的操作。网络错误和 5xx 不会删除会话，也不会无限重试。
- 确认框的昵称行使用 `type="nickname"` 的原生能力。`wx.login` 不会自动返回真实微信昵称，用户必须点击昵称行并选择“使用微信昵称”；本示例不使用已废弃的 `getUserProfile`。微信昵称审核失败或超时时会清空草稿并阻止登录，用户可重新选择或手动填写。登录中会阻止重复确认和关闭。
- 页面打开时会恢复已有会话并展示用户信息；手动刷新会先刷新 token，再读取 UserInfo。`/connect/userinfo` 返回 401/`invalid_token` 时最多恢复并重试一次；`400`、网络错误和 `5xx` 不会删除本机会话。
- Token 保存在 `wx` 小程序本地存储中；这只是业务本地存储，不是经认证的安全存储。示例不打印 token、微信 `code` 或服务端响应。

“清除本机会话”只删除当前设备上的小程序存储，不会全局撤销 NexusAuth 会话或 refresh token。

`wx.login` 只提供微信身份登录凭证，不会返回头像或昵称。登录页在确认登录成功后展示的昵称来自本次 token 响应中的 `user`，旧 Host 或资料恢复场景来自 NexusAuth UserInfo；本示例不处理头像。

## 测试

测试使用 Node.js 内置的 `node:test`，不安装任何依赖。在本目录运行：

```bash
node --test tests/*.test.js
```
