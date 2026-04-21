import { Navigate, Outlet } from 'react-router-dom';
import { useAuthStore } from '../store/useAuthStore';

export const PublicRoute = () => {
  const { isAuthenticated, user } = useAuthStore();

  if (isAuthenticated) {
    if (user?.status === 'Pending') return <Navigate to="/pending-approval" replace />;
    
    return <Navigate to="/" replace />;
  }

  return <Outlet />;
};