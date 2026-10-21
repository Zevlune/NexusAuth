import type { TokenEndpointAuthMethod } from '../../api/client';

export type ClientFormOption = {
  label: string;
  value: string;
  description?: string;
};

export const authMethodOptions: ClientFormOption[] = [
  { label: 'client_secret_basic', value: 'client_secret_basic' },
  { label: 'client_secret_post', value: 'client_secret_post' },
  { label: 'client_secret_jwt', value: 'client_secret_jwt' },
  { label: 'private_key_jwt', value: 'private_key_jwt' },
  { label: 'none (public client)', value: 'none' },
];

export const grantTypeOptions: ClientFormOption[] = [
  { label: 'authorization_code', value: 'authorization_code' },
  { label: 'client_credentials', value: 'client_credentials' },
  { label: 'refresh_token', value: 'refresh_token' },
  { label: 'urn:ietf:params:oauth:grant-type:device_code', value: 'urn:ietf:params:oauth:grant-type:device_code' },
  {
    label: 'urn:nexusauth:grant-type:wechat_miniprogram',
    value: 'urn:nexusauth:grant-type:wechat_miniprogram',
  },
];

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

export const canManageClientCredentials = (tokenEndpointAuthMethod: TokenEndpointAuthMethod) =>
  tokenEndpointAuthMethod !== 'none';
