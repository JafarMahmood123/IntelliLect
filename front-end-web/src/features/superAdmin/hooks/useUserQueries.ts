import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import {
  searchUsers,
  getUserDetail,
  changeUserStatus,
  bulkChangeUserStatus,
} from '../api/users';
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

/**
 * Accept/reject/deactivate/reactivate many accounts at once.
 *
 * Every detail page in the batch is invalidated, not just the list — a super admin who acts on
 * the directory and then opens one of those accounts must not be shown its old status.
 */
export const useBulkChangeUserStatus = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userIds, action }: { userIds: string[]; action: UserStatusAction }) =>
      bulkChangeUserStatus(userIds, action),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      await queryClient.invalidateQueries({ queryKey: ['user-detail'] });
    },
  });
};
