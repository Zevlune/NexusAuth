# APISIX Admin API 手工配置 NexusAuth OIDC 手册

> 状态：历史参考。Workbench 已移除网关接入和 Bearer 认证模式，本文将网关令牌转发给 Workbench 的示例不适用于当前版本。当前 Workbench 使用 Cookie + OIDC BFF，部署见 [Workbench 使用手册](./Workbench使用手册.md)；独立认证服务见 [Host 使用手册](./NexusAuth.Host使用手册.md)。

当前本地已有的网关 OIDC 路由为 `http://localhost:9180/auth/me`，回调为 `http://localhost:9180/auth/callback`，进入时会跳转到 `http://localhost:5100` 登录。下文 `/api/*` 是另建路由的手工示例，并非当前已部署路由。根 Compose 必须先运行；本地启动使用 `./docker/apisix/start-local.sh`。APISIX 的数据面只绑定宿主机回环地址，Admin API 不映射到宿主机。

本文只通过 APISIX Admin API 配置网关，不使用 NexusAuth 启动初始化自动创建网关应用或路由。`docker-compose.apisix.yml` 只负责启动 APISIX 和 etcd。旧版 APISIX Dashboard 3.0 会直接把 JSON 写入 etcd，无法参与 APISIX 的字段加密流程，因此不再随 Compose 部署。请先在 Workbench Dashboard 创建网关 OAuth 应用并生成密钥；Workbench API 重启不会修改已有网关应用。

## 1. 最终访问关系

```text
浏览器 -> http://localhost:9180/api/* -> APISIX -> admin-api:8080
                         |
                         | 未登录时 302
                         v
              http://localhost:5100/connect/authorize
                         |
                         | 登录成功后回调
                         v
              http://localhost:9180/api/callback
```

| 组件 | 地址 | 是否由 APISIX 对外代理 |
| --- | --- | --- |
| NexusAuth SSO | `http://localhost:5100` | 否，浏览器直接访问 |
| Workbench Dashboard | `http://localhost:5560` | 否 |
| APISIX Gateway | `http://localhost:9180` | 是，承载业务 API |

## 2. 启动基础容器

本地环境先启动根目录的 NexusAuth Compose，再启动 APISIX。启动脚本会生成并复用忽略提交的 `.env.apisix.local` 管理密钥，并要求通过外部环境变量提供稳定的 16 位十六进制 `APISIX_DATA_ENCRYPTION_KEY`；它还会自动创建 `localhost:5100` 的内部回源路由：

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d
# 先从密钥管理系统加载并导出已保存的字段加密 key。
: "${APISIX_DATA_ENCRYPTION_KEY:?请提供已持久保存的字段加密 key}"
./docker/apisix/start-local.sh
```

仅首次部署时可用 `openssl rand -hex 8` 生成字段加密 key，并立即保存到密钥管理系统。不要在每次启动命令中重新生成它；已有 etcd 数据必须使用原 key 解密。

APISIX 同时加入根 Compose 的 `nexusauth_default` 网络和独立的内部 etcd 网络：前者只用于访问 `sso:8080` 与 `admin-api:8080`，后者只用于访问 etcd。etcd 不映射宿主机端口。执行脚本前可自行设置至少 32 字符的 `APISIX_ADMIN_KEY` 环境变量；不要把管理密钥或字段加密 key 提交到仓库。字段加密 key 必须在重启和迁移时保持不变。新写入的 `/auth/*` OIDC 路由敏感字段会由 APISIX 加密后保存；旧卷中由 Dashboard 或直接 etcd 写入的历史明文不会自动迁移，必须备份后通过 Admin API 重写敏感字段。启动脚本不会创建或重置已有路由；全新部署需先在 Workbench 创建 Client、密钥与路由。

检查状态：

```bash
docker compose ps
docker ps --filter name=nexusauth-apisix
```

如果自行设置了环境变量而未生成 `.env.apisix.local`，检查状态时继续使用同一个变量。

APISIX Admin API 只在容器内部监听。需要读取或修改路由、Plugin Config 时，在受信任的管理网络中调用 Admin API，并通过 `X-API-KEY: $APISIX_ADMIN_KEY` 认证；不要直接读写 etcd。`start-local.sh` 已用同一 API 写入回源路由。

## 3. NexusAuth Client 前置检查

本地 HTTP 演示须显式使用 `docker-compose.local.yml`，以关闭 HTTPS metadata 要求并设置 `SameAsRequest` Cookie；生产部署保留 HTTPS 和 Secure Cookie 默认值。

先打开 Workbench：`http://localhost:5560`，进入应用管理。若不存在 Client `nexusauth.apisix.gateway`，先在 Workbench Dashboard 创建一个 Confidential client、生成密钥并登记回调；不要依赖 Workbench API 启动初始化创建网关应用。已有网关应用不会因 API 重启而修改。

必须满足以下条件：

| 配置项 | 要求 |
| --- | --- |
| Client ID | `nexusauth.apisix.gateway` |
| Client 类型 | Confidential client |
| Client secret | 已创建，并且当前操作者知道明文值 |
| Token endpoint auth method | `client_secret_basic` |
| Grant types | `authorization_code`、`refresh_token` |
| PKCE | 启用，S256 |
| Redirect URI | `http://localhost:9180/api/callback` |
| Post logout redirect URI | `http://localhost:9180/` |
| Identity scopes | `openid profile offline_access` |
| API scope | `nexusauth.workbench.api`，或目标服务的 audience |
| 状态 | Active |

Client secret 只提交给受保护的 APISIX Admin API，不写入前端代码或文档。Admin API 会让 APISIX 按 `data_encryption.keyring` 配置加密后保存敏感字段；不要直接向 etcd 写 JSON。

如果以后保护订单服务，应先在 NexusAuth 创建 audience 为 `orders-api` 的服务资源，并把该资源绑定到这个 Client。APISIX 中填写 scope 不会自动创建 NexusAuth 授权关系。

## 4. 创建内部 SSO Backchannel Route

这一步只为本地无域名环境服务。

NexusAuth discovery 的 issuer 和端点是 `http://localhost:5100`。浏览器访问这个地址时直接进入 SSO 容器；但 APISIX 容器访问 `localhost:5100` 时，localhost 指向 APISIX 自己。因此 APISIX 配置为在容器内部监听 `5100`，需要一条仅匹配该端口的回源路由。

`start-local.sh` 会自动通过 APISIX Admin API 写入这条路由，不需要手工创建。可通过 Admin API 查询路由，核心配置应为：

| 字段 | 值 |
| --- | --- |
| Route ID | `nexusauth-sso-backchannel` |
| Name | `NexusAuth SSO Backchannel` |
| URI | `/*` |
| Priority | `100` |
| Status | Enabled |
| Plugin | 不添加任何认证插件 |

在高级匹配条件中增加：

| Variable | Operator | Value |
| --- | --- | --- |
| `server_port` | `==` | `5100` |

配置 Upstream：

| 字段 | 值 |
| --- | --- |
| Type | `roundrobin` |
| Scheme | `http` |
| Node host | `sso` |
| Node port | `8080` |
| Weight | `1` |

保存后，这条 Route 的核心配置应等价于：

```json
{
  "id": "nexusauth-sso-backchannel",
  "name": "NexusAuth SSO Backchannel",
  "uri": "/*",
  "vars": [["server_port", "==", "5100"]],
  "priority": 100,
  "upstream": {
    "type": "roundrobin",
    "nodes": { "sso:8080": 1 }
  }
}
```

不要使用 `hosts: ["localhost"]` 代替端口条件，否则浏览器访问 `localhost:9180/api/*` 时也可能命中这条 Route。

## 5. 创建可复用 OIDC Plugin Config

参考资料：

- [APISIX openid-connect 官方文档](https://apisix.apache.org/docs/apisix/plugins/openid-connect/)
- 本项目当前容器版本：APISIX `3.11.0`

### 5.1 必填字段清单

官方 schema 的硬性必填字段只有三个；但完成 NexusAuth 浏览器登录还需要一组场景必填字段：

| 字段 | 官方 schema 必填 | 本方案是否必须显式填写 | 本方案值 | 原因 |
| --- | --- | --- | --- | --- |
| `client_id` | 是 | 是 | `nexusauth.apisix.gateway` | 标识 NexusAuth Client |
| `client_secret` | 是 | 是 | Client 的真实 secret | APISIX 在 token endpoint 认证自身 |
| `discovery` | 是 | 是 | `http://sso:8080/.well-known/openid-configuration` | 获取授权、token、userinfo 和 JWKS 端点 |
| `session.secret` | 浏览器模式需要 | 是 | 32 字节以上随机值 | 加密并签名 APISIX 浏览器会话；至少 16 字符 |
| `redirect_uri` | 否 | 是 | `http://localhost:9180/api/callback` | 必须与 NexusAuth Client 登记值完全一致 |
| `scope` | 否，默认 `openid` | 是 | `openid profile offline_access nexusauth.workbench.api` | 申请身份信息、刷新令牌和目标 API audience |
| `use_pkce` | 否，默认 `false` | 是 | `true` | NexusAuth Client 已要求 PKCE/S256 |
| `bearer_only` | 否，默认 `false` | 是，建议显式填写 | `false` | 允许未登录浏览器跳转 SSO，而不是只返回 401 |
| `unauth_action` | 否，默认 `auth` | 是，建议显式填写 | `auth` | 未认证时执行授权跳转 |
| `token_endpoint_auth_method` | 否 | 是，建议显式填写 | `client_secret_basic` | 与 NexusAuth Client 认证方式保持一致 |
| `ssl_verify` | 否，默认 `false` | 本地是 | `false` | 当前 SSO 是 HTTP；生产必须使用 HTTPS 并设为 `true` |
| `set_access_token_header` | 否，默认 `true` | 是，建议显式填写 | `true` | 把 access token 交给上游 API |
| `access_token_in_authorization_header` | 否，默认 `false` | 是 | `true` | 上游通过标准 `Authorization: Bearer` 验证 JWT |

结论：如果只看 APISIX schema，必填是 `client_id`、`client_secret`、`discovery`；如果要让当前 NexusAuth 登录链路真正可用，请把表中“本方案必须”的字段全部填写。

### 5.2 通过 Admin API 写入

向 APISIX Admin API 的 `/apisix/admin/plugin_configs/nexus-oidc-workbench` 发送带 `X-API-KEY` 的 `PUT` 请求。请求体使用下面的 JSON，敏感字段只在受控终端输入：

| 字段 | 值 |
| --- | --- |
| ID | `nexus-oidc-workbench` |
| Description | `NexusAuth OIDC for Workbench API` |

请求体中的 `openid-connect` 插件字段如下：

| OIDC 字段 | 值 | 说明 |
| --- | --- | --- |
| `client_id` | `nexusauth.apisix.gateway` | 已有 NexusAuth Client |
| `client_secret` | Client 的真实 secret | 必须与 NexusAuth 中保存的凭据一致 |
| `discovery` | `http://sso:8080/.well-known/openid-configuration` | APISIX 容器访问地址 |
| `redirect_uri` | `http://localhost:9180/api/callback` | 必须与 Client 登记值完全一致 |
| `scope` | `openid profile offline_access nexusauth.workbench.api` | 按目标 API audience 调整 |
| `use_pkce` | `true` | NexusAuth Client 要求 PKCE |
| `bearer_only` | `false` | 未登录浏览器需要跳转登录页 |
| `unauth_action` | `auth` | 未登录时启动 OIDC 授权流程 |
| `token_endpoint_auth_method` | `client_secret_basic` | 与 Client 配置一致 |
| `ssl_verify` | `false` | 仅限当前 HTTP 本地开发环境 |
| `set_access_token_header` | `true` | 将 access token 传给上游 |
| `access_token_in_authorization_header` | `true` | 使用 `Authorization: Bearer` |
| `set_id_token_header` | `false` | 上游通常不需要 ID token |
| `set_userinfo_header` | `true` | 需要时传递用户信息 |
| `session.secret` | 独立的 32 字节以上随机值 | 不要与 Client secret 相同 |

可用以下命令只生成 session secret，然后放入 Admin API 请求体：

```bash
openssl rand -base64 48
```

至少必须写入 `client_id`、`client_secret`、`discovery`、`redirect_uri`、`scope`、`use_pkce=true`、`bearer_only=false`、`session.secret`。保存后必须通过实际 302 验证。不要把请求体保存到仓库或日志。

### 5.3 完整 JSON 模板

下面的 JSON 保留为核对清单。将两个 `REPLACE_...` 替换成真实值；不要把替换后的 secret 提交到 Git。

```json
{
  "id": "nexus-oidc-workbench",
  "desc": "NexusAuth OIDC for Workbench API",
  "plugins": {
    "openid-connect": {
      "client_id": "nexusauth.apisix.gateway",
      "client_secret": "REPLACE_WITH_NEXUSAUTH_CLIENT_SECRET",
      "discovery": "http://sso:8080/.well-known/openid-configuration",
      "redirect_uri": "http://localhost:9180/api/callback",
      "scope": "openid profile offline_access nexusauth.workbench.api",
      "use_pkce": true,
      "bearer_only": false,
      "unauth_action": "auth",
      "token_endpoint_auth_method": "client_secret_basic",
      "ssl_verify": false,
      "set_access_token_header": true,
      "access_token_in_authorization_header": true,
      "set_id_token_header": false,
      "set_userinfo_header": true,
      "set_refresh_token_header": false,
      "session": {
        "secret": "REPLACE_WITH_RANDOM_SESSION_SECRET_MIN_32_CHARS"
      }
    }
  }
}
```

APISIX Admin API 返回的 JSON 可能自动补充默认字段，且敏感字段可能在响应中被解密，这是正常现象。判断是否正确时，以 `client_id`、discovery、redirect URI、scope、PKCE、会话 secret 和未登录跳转行为为准；不要使用 etcd 原始值作为验证接口。

APISIX 的插件清单和全局规则可通过 Admin API 查询。创建 Plugin Config 不会把 `openid-connect` 标记为全局启用，这是正常状态；实际生效由 Route 引用该 Plugin Config 决定。

## 6. 创建业务 API Route

向 APISIX Admin API 的 `/apisix/admin/routes/nexus-api-gateway` 发送带 `X-API-KEY` 的 `PUT` 请求：

| 字段 | 值 |
| --- | --- |
| Route ID | `nexus-api-gateway` |
| Name | `Nexus API Gateway` |
| URI | `/api/*` |
| Priority | `200` |
| Status | Enabled |
| Plugin Config | `nexus-oidc-workbench` |

配置 Upstream：

| 字段 | 值 |
| --- | --- |
| Type | `roundrobin` |
| Scheme | `http` |
| Node host | `admin-api` |
| Node port | `8080` |
| Weight | `1` |

Route 上不要再次添加一份 `openid-connect`，否则会出现两套配置来源。Route 只引用 `plugin_config_id=nexus-oidc-workbench`。

保存后的核心结构应等价于：

```json
{
  "id": "nexus-api-gateway",
  "name": "Nexus API Gateway",
  "uri": "/api/*",
  "priority": 200,
  "plugin_config_id": "nexus-oidc-workbench",
  "upstream": {
    "type": "roundrobin",
    "nodes": { "admin-api:8080": 1 }
  }
}
```

## 7. 验证登录流程

先清除 `localhost:9180` 的旧 Cookie或使用浏览器隐私窗口，然后访问：

```text
http://localhost:9180/api/auth/me
```

正确流程：

1. APISIX 判断没有有效会话。
2. APISIX 返回 `302`。
3. 浏览器跳转到 `http://localhost:5100/connect/authorize`。
4. URL 中包含 `client_id=nexusauth.apisix.gateway`。
5. URL 中包含 `code_challenge` 和 `code_challenge_method=S256`。
6. 登录 NexusAuth。
7. NexusAuth 回调 `http://localhost:9180/api/callback`。
8. APISIX 使用授权码和 PKCE verifier 换取 token，并写入 session Cookie。
9. APISIX 恢复原始 API 请求并转发到 `admin-api:8080`。

只验证第一段跳转可以执行：

```bash
curl -I http://localhost:9180/api/auth/me
```

预期是 `HTTP/1.1 302`，`Location` 指向 `http://localhost:5100/connect/authorize`。

## 8. 前端跨端口访问

Workbench 页面在 `http://localhost:5560`，网关在 `http://localhost:9180`。前端请求必须携带 Cookie：

```ts
const api = axios.create({
  baseURL: "http://localhost:9180/api",
  withCredentials: true,
});
```

如果浏览器报告 CORS 错误，在 `nexus-oidc-workbench` Plugin Config 中再添加 `cors`：

| 字段 | 建议值 |
| --- | --- |
| `allow_origins` | `http://localhost:5560` |
| `allow_methods` | `GET,POST,PUT,PATCH,DELETE,OPTIONS` |
| `allow_headers` | `Content-Type,Authorization` |
| `allow_credential` | `true` |

启用凭据时不能使用 `allow_origins=*`。

## 9. 新增其他服务

同一个 audience 的 Route 可以复用 `nexus-oidc-workbench`。如果新服务使用不同 audience，例如 `orders-api`：

1. 在 NexusAuth 创建并启用 `orders-api` 服务资源。
2. 将 `orders-api` 绑定给 `nexusauth.apisix.gateway` Client。
3. 创建新的 Plugin Config，例如 `nexus-oidc-orders`。
4. scope 填 `openid profile offline_access orders-api`。
5. 创建 `/orders/*` Route，引用 `nexus-oidc-orders`。
6. Upstream 指向订单服务容器名和端口。

不要让一条业务 Route 同时申请多个无关 audience。后端还应验证 JWT 的签名、issuer、自己的 audience、过期时间和 `token_use=access_token`。

## 10. 常见故障

| 现象 | 原因与处理 |
| --- | --- |
| Admin API 返回 401 | 检查 `X-API-KEY` 是否与 `APISIX_ADMIN_KEY` 一致，以及请求是否来自受信任管理网络。 |
| 请求返回 404 | 检查业务 Route URI、状态、Upstream；确认 backchannel Route 只匹配端口 5100。 |
| 没有跳转登录页 | 检查 `bearer_only=false`、Plugin Config 已被 Route 引用，并清除旧 session Cookie。 |
| 授权地址没有 `code_challenge` | `use_pkce` 未启用。 |
| `invalid_redirect_uri` | NexusAuth Client 与插件的回调地址不完全一致。 |
| `invalid_client` | Client ID 或 secret 错误，或认证方式不是 `client_secret_basic`。 |
| `invalid_scope` | Client 未允许该 scope，或没有绑定对应 API resource。 |
| 回调后 500 / 无法换 token | 检查 `nexusauth-sso-backchannel`、`server_port == 5100` 和 `sso:8080` 网络连通性。 |
| 上游仍返回 401 | 设置 `access_token_in_authorization_header=true`，并检查后端 audience 与 issuer 校验。 |
| 浏览器 CORS 错误 | 添加精确 origin 的 `cors` 插件并允许 credentials。 |

查看运行日志：

```bash
docker compose -f docker-compose.apisix.yml logs --tail=200 apisix
docker compose -f docker-compose.apisix.yml logs --tail=200 apisix-etcd
```

## 11. 生产环境调整

- 使用真实 HTTPS 域名，不再使用本地 `5100` backchannel Route。
- issuer、discovery 中的端点和 redirect URI 必须使用浏览器与 APISIX 都能访问的 HTTPS 地址。
- `ssl_verify=true`，session Cookie 使用 Secure。
- APISIX Admin API 不对公网开放。
- Client secret、session secret、Admin key 使用 Vault、Kubernetes Secret 或等价密钥管理服务。
- APISIX 字段加密 key 使用 Vault、Kubernetes Secret 或等价密钥管理服务，并在轮换时保留旧 key 以解密已有数据。
