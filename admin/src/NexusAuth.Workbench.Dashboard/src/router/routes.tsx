import { Navigate, RouteObject } from 'react-router-dom';

import ErrorPage from '../components/error';
import GlobalLoading from '../components/global-loading';
import PublicLayout from '../layouts/layout';
import Login from '../pages/login';
import Dashboard from '../pages/dashboard';
import { getCachedAuthStatus, HomeRedirect, RedirectIfAuthenticated, RequireAuth } from './auth';

const AuthCallback = () => {
  return <Navigate to={getCachedAuthStatus() ? '/dashboard' : '/login?error=session_missing'} replace />;
};

export const routes: RouteObject[] = [
  {
    element: <RedirectIfAuthenticated />,
    children: [
      {
        path: '/login',
        Component: Login,
      },
      {
        path: '/auth/callback',
        Component: AuthCallback,
      },
    ],
  },
  {
    path: '/',
    element: <HomeRedirect />,
  },
  {
    element: <RequireAuth />,
    children: [
      {
        id: 'protected-layout',
        path: '/',
        Component: PublicLayout,
        children: [
          {
            path: 'dashboard',
            Component: Dashboard,
          },
          {
            path: '*',
            Component: GlobalLoading,
          },
        ],
        errorElement: <ErrorPage />,
      },
    ],
  },
];
