import { Navigate, Outlet, useLocation } from 'react-router-dom';

import {
  getCachedAuthStatus,
  getLoginPath,
  getSafeRedirectPath,
} from './auth';

export const RequireAuth = () => {
  const location = useLocation();

  if (!getCachedAuthStatus()) {
    return <Navigate to={getLoginPath(location)} replace state={{ from: location }} />;
  }

  return <Outlet />;
};

export const RedirectIfAuthenticated = () => {
  const location = useLocation();

  if (getCachedAuthStatus()) {
    const redirect = getSafeRedirectPath(new URLSearchParams(location.search).get('redirect'));
    return <Navigate to={redirect || '/dashboard'} replace />;
  }

  return <Outlet />;
};

export const HomeRedirect = () => {
  return <Navigate to={getCachedAuthStatus() ? '/dashboard' : '/login'} replace />;
};

export const AuthCallback = () => {
  return <Navigate to={getCachedAuthStatus() ? '/dashboard' : '/login?error=session_missing'} replace />;
};
