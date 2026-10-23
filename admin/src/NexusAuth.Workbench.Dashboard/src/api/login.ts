import request from './request';

export interface UserInfo {
  id: string;
  name: string;
}

export interface LoginResponse {
  isAuthenticated: boolean;
  /** 当前账号是否为管理员；后端未返回该字段时视为未知。 */
  isAdministrator?: boolean;
  user: UserInfo;
}

export interface AuthStatus {
  isAuthenticated: boolean;
  /** null 表示未知（未登录或后端未返回 isAdministrator）。 */
  isAdministrator: boolean | null;
}

export interface ConfigResponse {
  authority: string;
  clientId: string;
}

export interface LogoutResponse {
  logoutUrl: string;
}

export const getConfig = (): Promise<ConfigResponse> => {
  return request.get<ConfigResponse>('/auth/config');
};

export const startLogin = (): Promise<{ authorizeUrl: string }> => {
  return request.get<{ authorizeUrl: string }>('/auth/login');
};

export const getCurrentUser = (): Promise<LoginResponse> => {
  return request.get<LoginResponse>('/auth/me');
};

export const logout = (): Promise<LogoutResponse> => {
  return request.post<LogoutResponse>('/auth/logout');
};

export const checkAuth = async (): Promise<AuthStatus> => {
  try {
    const result = await getCurrentUser();
    return {
      isAuthenticated: result.isAuthenticated === true,
      isAdministrator: typeof result.isAdministrator === 'boolean' ? result.isAdministrator : null,
    };
  } catch {
    return { isAuthenticated: false, isAdministrator: null };
  }
};
