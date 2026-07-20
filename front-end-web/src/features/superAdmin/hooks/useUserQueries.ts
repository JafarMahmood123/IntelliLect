import { useQuery, keepPreviousData } from '@tanstack/react-query';
import { searchUsers, getUserDetail } from '../api/users';
import type { SearchUsersParams } from '../types';

// Paged/filtered directory of all platform users.
export const useUsers = (params: SearchUsersParams) => {
  return useQuery({
    queryKey: ['users', params],
    queryFn: () => searchUsers(params),
    placeholderData: keepPreviousData,
    staleTime: 15_000,
  });
};

// A single user's details plus their classroom memberships.
export const useUserDetail = (userId: string | undefined) => {
  return useQuery({
    queryKey: ['user-detail', userId],
    queryFn: () => getUserDetail(userId as string),
    enabled: !!userId,
  });
};
