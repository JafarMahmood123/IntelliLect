import { useQuery } from '@tanstack/react-query';
import { getRegistrationRoles } from '../api/roles';

export const useRegistrationRoles = () => {
  return useQuery({
    queryKey: ['registration-roles'],
    queryFn: getRegistrationRoles,
    staleTime: 5 * 60 * 1000,
    retry: 1,
  });
};