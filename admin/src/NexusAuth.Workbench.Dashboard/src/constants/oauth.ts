export const OAuthClientAuthenticationMethods = {
  ClientSecretBasic: 'client_secret_basic',
  ClientSecretPost: 'client_secret_post',
  ClientSecretJwt: 'client_secret_jwt',
  PrivateKeyJwt: 'private_key_jwt',
  None: 'none',
} as const;

export const OAuthGrantTypes = {
  AuthorizationCode: 'authorization_code',
  ClientCredentials: 'client_credentials',
  RefreshToken: 'refresh_token',
  DeviceCode: 'urn:ietf:params:oauth:grant-type:device_code',
  WeChatMiniProgram: 'urn:nexusauth:grant-type:wechat_miniprogram',
} as const;
