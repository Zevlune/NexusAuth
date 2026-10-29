# Workbench Dashboard：浏览器统一登录模板

这个模板展示 React 前端如何与已接入 NexusAuth 的服务端配合。Dashboard 的页面脚本不兑换授权码，不验证 ID Token，也不保存客户端密钥或明文令牌；浏览器通过 API 签发的加密 HttpOnly Cookie 维持会话。整套管理工作台的部署与操作先看 [Workbench 使用手册](Workbench使用手册.md)。对应代码在 [`admin/src/NexusAuth.Workbench.Dashboard`](../admin/src/NexusAuth.Workbench.Dashboard)；后端步骤见[Workbench API 模板](./Workbench-API统一登录接入模板.md)。

## 页面如何进入登录状态

1. 前端先请求同源 `GET /api/auth/me`。已登录时读取最小用户信息；收到 401 就显示 `/login`。
2. 用户点击“使用 NexusAuth 登录”，前端请求 `GET /api/auth/login`，取得服务端生成的 `authorizeUrl`，再用 `window.location.assign` 跳转。`state`、`nonce` 和 PKCE verifier 由 API 保存，前端不自行拼接敏感参数。
3. NexusAuth 把浏览器重定向到 API 的 `/signin-oidc`，由 API 的原生 OpenID Connect handler 兑换授权码、验证 ID Token 并设置 HttpOnly Cookie；随后浏览器跳回前端 `/auth/callback` 落地页。
4. 前端重新请求 `/api/auth/me`，成功后进入受保护页面。普通管理请求使用同源 Cookie；受保护 API 返回 401 时回到登录页。
5. 登出时调用 `POST /api/auth/logout` 清 API 会话；若响应包含 `logoutUrl`，浏览器继续跳到 Discovery 声明的 `end_session_endpoint`，完成 Provider 侧登出。

接口由 [`src/api/login.ts`](../admin/src/NexusAuth.Workbench.Dashboard/src/api/login.ts) 封装；路由和登录状态判断见 [`src/router/auth.ts`](../admin/src/NexusAuth.Workbench.Dashboard/src/router/auth.ts) 与 [`src/router/routes.tsx`](../admin/src/NexusAuth.Workbench.Dashboard/src/router/routes.tsx)。`/auth/callback` 是前端落地页，不是 NexusAuth 登记的 OAuth 回调地址。不要把客户端的 `redirect_uri` 指向它。

## 同源部署和 Cookie

浏览器通过 `/api` 访问后端，请求开启 `withCredentials`。当前 Vite 配置将 `/api` 与 `/signin-oidc` 转发到本地 API `http://localhost:5051`；API Development 配置的 `Auth:Authority` 默认为 `http://localhost:5100`。若 Host 不在该地址，只改本机的 Authority 指向，不要改变登录回调链路。根目录 Compose 的 Nginx 同样代理这两个路径，因此对外回调为 `http://localhost:5560/signin-oidc`，真正处理回调的是 Workbench API。直接运行 Dashboard 时默认 Vite 端口为 `5273`，Compose 为 `5560`。配置的 `redirect_uri` 必须与公开路径逐字符一致。

浏览器 Dashboard 只使用 API 签发的 HttpOnly Cookie；Bearer 用于非浏览器管理 API 客户端，页面脚本不需要也不应从登录响应、Cookie、查询串、localStorage 或构建变量中取得 access token 或 refresh token。应用请求收到 401 时可清理页面登录状态并回到登录入口，但不要对 `/api/auth/me` 的 401 反复跳转，否则会形成登录循环。复制这个模板到其他应用时，后端必须先实现对应的 `/api/auth/*`、回调、Cookie 验证与刷新逻辑；单独复制 React 页面无法完成 OIDC 接入。

## 直接对照

- [`src/pages/login/index.tsx`](../admin/src/NexusAuth.Workbench.Dashboard/src/pages/login/index.tsx)：发起登录。
- [`src/api/login.ts`](../admin/src/NexusAuth.Workbench.Dashboard/src/api/login.ts)：登录状态、退出请求。
- [`src/api/request.ts`](../admin/src/NexusAuth.Workbench.Dashboard/src/api/request.ts)：同源 Cookie 请求和 401 处理。
- [`nginx.conf`](../admin/src/NexusAuth.Workbench.Dashboard/nginx.conf) 与 [`vite.config.ts`](../admin/src/NexusAuth.Workbench.Dashboard/vite.config.ts)：`/api`、`/signin-oidc` 反向代理。
- [Dashboard 项目 README](../admin/src/NexusAuth.Workbench.Dashboard/README.md)：运行与构建命令。
