module.exports = {
  // 替换为已在微信小程序后台登记为 request 合法域名的 NexusAuth.Host HTTPS 地址。
  API_BASE: 'https://sso.example.com',

  // 替换为 NexusAuth 中公共客户端记录对应的 client_id。
  CLIENT_ID: 'wechat-miniapp',

  // 客户端 allowed_scopes 中必须包含以下 scope。
  SCOPE: 'openid profile offline_access',

  REQUEST_TIMEOUT_MS: 15000
};
