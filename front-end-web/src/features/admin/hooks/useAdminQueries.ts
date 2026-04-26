import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  getPendingRequests,
  getAllUsers,
  updateUserStatus,
  deactivateUser,
  reactivateUser,
} from '../api/admin';
import type { GetUsersParams, UserStatusPayload } from '../types';

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