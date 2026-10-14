import { checkAuth } from '../api/login';

let cachedAuthStatus: boolean | null = null;

export const getCachedAuthStatus = () => cachedAuthStatus;

export const setCachedAuthStatus = (status: boolean) => {
  cachedAuthStatus = status;
};

export const checkAuthenticated = async () => {
  try {
    const isAuth = await checkAuth();
    setCachedAuthStatus(isAuth);
    return isAuth;
  } catch {
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
