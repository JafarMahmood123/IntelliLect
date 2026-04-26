import { Navigate, Outlet } from 'react-router-dom';
import { useAuthStore } from '../store/useAuthStore';
import { getDefaultRoute } from '../utils/getDefaultRoute';

export const PublicRoute = () => {
  const { isAuthenticated, user } = useAuthStore();

  if (isAuthenticated) {
    return <Navigate to={getDefaultRoute(user)} replace />;
  }

  return <Outlet />;
};