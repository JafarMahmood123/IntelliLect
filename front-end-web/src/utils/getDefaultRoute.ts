import type { User } from '../types';

export const getDefaultRoute = (user: User | null | undefined) => {
  if (!user) return '/login';

  if (user.status === 'Pending') {
    return '/pending-approval';
  }

  if (user.roleName === 'SuperAdmin') {
    return '/super-admin';
  }

  if (user.roleName === 'Admin') {
    return '/admin';
  }

  return '/';
};