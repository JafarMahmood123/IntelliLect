import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import { searchUsers, getUserDetail, changeUserStatus } from '../api/users';
import type { SearchUsersParams, UserStatusAction } from '../types';

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

// Accept/reject/deactivate/reactivate a user account.
export const useChangeUserStatus = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, action }: { userId: string; action: UserStatusAction }) =>
      changeUserStatus(userId, action),
    onSuccess: async (_data, variables) => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      await queryClient.invalidateQueries({ queryKey: ['user-detail', variables.userId] });
    },
  });
};
