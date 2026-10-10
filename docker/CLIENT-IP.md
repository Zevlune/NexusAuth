# 登录记录的客户端 IP

Provider 在限流、认证和记录审计之前处理代理转发头。登录记录（密码、TOTP、Passkey）和登录 IP 限流共用处理后的地址。IPv4 映射 IPv6 地址会显示成普通 IPv4；历史记录只调整显示格式，不改写历史来源。

## 经过反向代理访问

仅信任实际代理节点，示例 `.env`（将示例地址替换为实际地址）：

```dotenv
NEXUSAUTH_CLIENT_IP_FORWARDED_HEADERS_ENABLED=true
NEXUSAUTH_CLIENT_IP_KNOWN_PROXIES=10.10.0.8
NEXUSAUTH_CLIENT_IP_KNOWN_NETWORKS=
NEXUSAUTH_CLIENT_IP_FORWARD_LIMIT=1
```

代理有多个固定地址时用逗号分隔；地址会变化时可配置专属代理子网，例如 `NEXUSAUTH_CLIENT_IP_KNOWN_NETWORKS=10.10.0.0/28`。不要信任整个公网或包含普通客户端的网络。启用转发但没有指定可信代理/网段会拒绝启动。

直接面对浏览器的 Nginx 入口应覆盖客户端提供的头：

```nginx
location / {
    proxy_pass http://sso:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

多级代理需从应用端到入口逐级配置可信节点，并设置对应跳数。应用从右向左处理 `X-Forwarded-For`，遇到不可信节点停止，不直接取可被客户端伪造的第一个地址。入口有上游 CDN 时，入口也必须先按照 CDN 的可信节点配置还原真实来源。

应用端口应只开放给可信代理。`X-Real-IP` 或任意客户端自行提交的 IP 不用作审计来源。

## 本机 Docker Desktop

当前站点通过 Docker Desktop 的端口映射直接访问，虚拟机/NAT 可能把客户端地址替换为网关地址。这个转发层没有提供原始客户端头，应用无法从网关地址推算浏览器的真实 IP。为避免伪造，默认保持转发头处理关闭；记录观察到的连接地址。

需要真实来源时，应在仍能看到客户端 IP 的入口部署反向代理（例如宿主机 Nginx），由该入口覆盖转发头，再配置其在容器内观察到的连接地址为可信代理。单纯把 Nginx 也放进同一个 Docker Desktop NAT 后面无法补回已经丢失的 IP。通过 localhost 访问时真实来源通常是 `127.0.0.1` 或 `::1`，而不是机器的公网地址。

历史记录中已丢失的原始来源无法追溯补回；新的代理配置只影响之后的请求。
