import { checkAuth } from '../api/login';

let cachedAuthStatus: boolean | null = null;
let cachedAdministratorStatus: boolean | null = null;

export const getCachedAuthStatus = () => cachedAuthStatus;

/** 当前账号是否为管理员；null 表示未知（例如后端未返回 isAdministrator）。 */
export const getCachedAdministratorStatus = () => cachedAdministratorStatus;

export const setCachedAuthStatus = (status: boolean) => {
  cachedAuthStatus = status;
};

export const checkAuthenticated = async () => {
  try {
    const { isAuthenticated, isAdministrator } = await checkAuth();
    cachedAdministratorStatus = isAuthenticated ? isAdministrator : null;
    setCachedAuthStatus(isAuthenticated);
    return isAuthenticated;
  } catch {
    cachedAdministratorStatus = null;
    setCachedAuthStatus(false);
    return false;
  }
};

export const getSafeRedirectPath = (redirect: string | null | undefined) => {
  if (!redirect || !redirect.startsWith('/') || redirect.startsWith('//')) {
    return null;
  }

  return redirect;
};

const getRedirectPath = (location: { pathname: string; search?: string; hash?: string }) => {
  return `${location.pathname}${location.search || ''}${location.hash || ''}`;
};

export const getLoginPath = (location: { pathname: string; search?: string; hash?: string }) => {
  const params = new URLSearchParams({ redirect: getRedirectPath(location) });
  return `/login?${params.toString()}`;
};
