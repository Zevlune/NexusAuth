import {
  OAuthClientAuthenticationMethods,
  OAuthGrantTypes,
} from '../../constants/oauth';
import type { TokenEndpointAuthMethod } from '../../api/client';

export type ClientFormOption = {
  label: string;
  value: string;
  description?: string;
};

export const authMethodOptions: ClientFormOption[] = Object.values(OAuthClientAuthenticationMethods)
  .map((value) => ({
    label: value === OAuthClientAuthenticationMethods.None ? `${value} (public client)` : value,
    value,
  }));

export const grantTypeOptions: ClientFormOption[] = Object.values(OAuthGrantTypes)
  .map((value) => ({ label: value, value }));

export const mergeOptions = (...groups: ClientFormOption[][]): ClientFormOption[] => {
  const merged = new Map<string, ClientFormOption>();
  groups.flat().forEach((option) => {
    if (!option.value || merged.has(option.value)) {
      return;
    }
    merged.set(option.value, option);
  });
  return Array.from(merged.values());
};

// 仅共享密钥类认证方式支持重置；private_key_jwt 需更新登记的 JWKS，none 不使用客户端凭据。
export const canResetClientCredential = (tokenEndpointAuthMethod: TokenEndpointAuthMethod) =>
  tokenEndpointAuthMethod === OAuthClientAuthenticationMethods.ClientSecretBasic
  || tokenEndpointAuthMethod === OAuthClientAuthenticationMethods.ClientSecretPost
  || tokenEndpointAuthMethod === OAuthClientAuthenticationMethods.ClientSecretJwt;
