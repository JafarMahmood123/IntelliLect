import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  getPendingRequests,
  getAllUsers,
  updateUserStatus,
  deactivateUser,
  reactivateUser,
  bulkUpdateUserStatus,
} from '../api/admin';
import type { GetUsersParams, UserStatusPayload, UserStatusAction } from '../types';

export const usePendingUsers = (params: GetUsersParams) => {
  return useQuery({
    queryKey: ['admin', 'pending-users', params],
    queryFn: () => getPendingRequests(params),
    placeholderData: (prev) => prev, 
  });
};

export const useAllUsers = (params: GetUsersParams) => {
  return useQuery({
    queryKey: ['admin', 'all-users', params],
    queryFn: () => getAllUsers(params),
    placeholderData: (prev) => prev,
  });
};

export const useUpdateUserStatus = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, status }: { id: string; status: UserStatusPayload }) =>
      updateUserStatus(id, status),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey:['admin', 'pending-users'] });
      queryClient.invalidateQueries({ queryKey:['admin', 'all-users'] });
    },
  });
};

/**
 * Applies one action to many accounts.
 *
 * Resolves rather than rejects when individual accounts failed — the request succeeded, some
 * accounts did not. Callers must inspect the result. It rejects only when the request itself was
 * refused (no ids, over the cap, unknown action).
 */
export const useBulkUpdateUserStatus = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ userIds, action }: { userIds: string[]; action: UserStatusAction }) =>
      bulkUpdateUserStatus(userIds, action),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'pending-users'] });
      queryClient.invalidateQueries({ queryKey: ['admin', 'all-users'] });
    },
  });
};

export const useDeactivateUser = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => deactivateUser(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'all-users'] });
    },
  });
};

export const useReactivateUser = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => reactivateUser(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'all-users'] });
    },
  });
};