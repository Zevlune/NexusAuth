# NexusAuth Workbench Dashboard

Workbench Dashboard 是 NexusAuth 管理工作台的浏览器界面。它只通过 Workbench API 的 BFF（`/api`）完成登录和管理操作，本身不是 OAuth 客户端，不持有客户端密钥。access token 和 refresh token 位于 API 签发的加密 HttpOnly 票据 Cookie 中，页面脚本读不到明文。

本组件属于 **Workbench 管理工作台**；NexusAuth.Host 是独立的身份认证服务器，两者分开部署。整套工作台的部署、配置、权限和排错见 [Workbench 使用手册](../../../document/Workbench使用手册.md)；服务端入口见 [Workbench API README](../../README.md)。

## 技术栈

- React 19、TypeScript、Vite 6；
- TDesign React 与 TDesign Icons；
- Axios（统一请求与 API 结果解包）；
- Vite 开发代理与 Nginx 生产静态托管。

## 可用命令

| 命令 | 作用 |
| --- | --- |
| `npm install` | 安装前端依赖 |
| `npm run dev` | 启动 Vite 开发服务器，默认 `http://localhost:5273` |
| `npm run build` | 执行 `tsc -b` 和 Vite 生产构建 |
| `npm run lint` | 执行 ESLint |
| `npm run preview` | 预览已构建的 `dist` |

没有单独的 `npm run typecheck` 脚本，类型检查包含在 `npm run build` 中。

## 本地开发

Dashboard 依赖已配置好的 Workbench API 和 Host。先按 [Workbench 使用手册](../../../document/Workbench使用手册.md) 启动数据库、Host 和 Workbench API，再在本目录启动前端：

```bash
npm install
npm run dev
```

打开 `http://localhost:5273`。`vite.config.ts` 当前把 `/api` 和 `/signin-oidc` 代理到 `http://192.168.31.200:9100`。本地运行 API 时，必须把这两个代理目标改成 API 的真实地址，例如 `http://localhost:5051`。API 默认监听 `http://localhost:5051`，Swagger 在 `http://localhost:5051/swagger`。

本地方式下 API 的 `Auth:Authority` 默认还是局域网地址 `http://192.168.31.200:8100`，也要改成实际 Host 地址。Provider 登记的 `redirect_uri` 要写成浏览器实际访问的 Vite 地址 `http://localhost:5273/signin-oidc`（由 Vite 代理到 API 的 `5051`），`post_logout_redirect_uri` 用 `http://localhost:5273/`。回调是浏览器地址，不是 API 监听端口。

## 登录流程

1. 应用启动时请求 `GET /api/auth/me` 检查当前 Workbench Cookie；未登录时显示 `/login`。
2. 登录页请求 `GET /api/auth/login`，拿到 `authorizeUrl` 后用 `window.location.assign` 跳转。`state`、`nonce` 和 PKCE verifier 由 API 服务端保存，前端不自行拼接敏感参数。
3. Host 回调 API 的 `GET /signin-oidc`；API 在服务端兑换授权码、校验 ID Token 并写入 HttpOnly Cookie。
4. API 跳转前端 `/auth/callback`，前端再次检查登录状态并进入 `/dashboard`。
5. 普通管理请求使用同源 Cookie；受保护请求返回 401 时回到登录页。
6. 退出调用 `POST /api/auth/logout` 清 Cookie；若响应含 `logoutUrl`，浏览器继续跳到 Host 的 `/connect/endsession`。

Cookie 名为 `.NexusAuth.Workbench`，是独立的 24 小时会话并开启滑动续期。加密票据里保存了 access/refresh/id token，以 `HttpOnly` Cookie 形式发给浏览器，页面脚本无法读取或解密。access token 临近过期时由 API 自动刷新、introspection 复检并轮换令牌；这些逻辑都在服务端。

## 请求与 API 结果

`src/api/request.ts` 创建了 `baseURL: '/api'`、`withCredentials: true` 的 Axios 实例：

- `/auth/` 请求的 401 不触发全局跳转，避免登录检查形成死循环；
- 其他受保护请求收到 401 时跳转 `/login`；
- 带 `success/result` 的管理 API 响应会自动解包，失败结果转成 Axios 错误；
- `/api/auth/*` 和 `/signin-oidc` 是认证流程端点，不依赖普通管理 API 的结果包装。

不要在源码、构建变量、localStorage 或 sessionStorage 中保存 `client_secret`、access token 或 refresh token。

## 菜单与路由

菜单定义在 `src/api/auth.ts`，路由由这些定义动态注册。当前可见菜单：

| 菜单 | 路由 |
| --- | --- |
| 仪表盘 | `/dashboard` |
| 用户管理 | `/user-management` |
| 应用中心 → 应用管理 | `/oauth/client-management` |
| 应用中心 → SCIM 凭证 | `/oauth/scim-credential-management` |
| 应用中心 → 开放 API 凭据 | `/oauth/open-api-credential-management` |
| 应用中心 → 登录记录 | `/oauth/login-audit-management` |
| 应用中心 → 服务资源 | `/oauth/api-resource-management` |
| 智 V V | `/ai-agent` |
| 验证配置 → 字段取值配置 / 产品配置 / saleRefId 映射配置 / 公司主体映射配置 / 字典配置 | `/field-value-mapping-config` 等 |

不在菜单中但仍在路由表内的地址：新增应用 `/oauth/client-management/create`、编辑应用 `/oauth/client-management/edit/:id`，以及兼容地址 `/oauth/user-management`。“公司主体映射配置”在菜单里有条目，但 `pages/` 下没有对应组件，打开会落到错误页。“智 V V”和“验证配置”下的页面属于扩展/占位模块，不是通用 OAuth 管理能力。

各页面的具体操作步骤见 [Workbench 使用手册第 9 节](../../../document/Workbench使用手册.md#9-管理对象与操作步骤)。

## 构建与 Docker

从仓库根目录执行：

```bash
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run build
npm --prefix admin/src/NexusAuth.Workbench.Dashboard run lint
```

Dashboard 镜像使用 Node 22 构建、Nginx 1.27 托管。根 Compose 将宿主 `5560` 映射到容器 `80`；`5273` 是本地 Vite 端口。Nginx 把 `/api` 和 `/signin-oidc` 代理到 `admin-api:8080`，其余路由回退到 `index.html`，因此前端刷新可用。

生产反向代理必须：

- 将 `/api` 转发到 Workbench API，并保留 `Host`、`X-Forwarded-Proto`、`X-Forwarded-Host` 和 Cookie；
- 使用 HTTPS；
- 把 Provider 登记的 OIDC 回调设置为实际对外提供 `/signin-oidc` 的地址；
- 通过 Workbench API 的环境变量注入客户端密钥，Dashboard 不读取该密钥。

## 相关文档

- [Workbench 使用手册](../../../document/Workbench使用手册.md)
- [Workbench API README](../../README.md)
- [Workbench Dashboard 统一登录接入模板](../../../document/Workbench-Dashboard统一登录接入模板.md)
- [Workbench API 统一登录接入模板](../../../document/Workbench-API统一登录接入模板.md)
- [NexusAuth.Host 使用手册](../../../document/NexusAuth.Host使用手册.md)
