import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  changePassword,
  getCurrentUser,
  updateCurrentUser,
} from '../api/users';
import type { ChangePasswordRequest, UpdateCurrentUserRequest } from '../types';

export const currentUserQueryKey = ['users', 'me'];

export const useCurrentUser = () => {
  return useQuery({
    queryKey: currentUserQueryKey,
    queryFn: getCurrentUser,
  });
};

export const useUpdateCurrentUser = () => {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (data: UpdateCurrentUserRequest) => updateCurrentUser(data),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: currentUserQueryKey });
    },
  });
};

export const useChangePassword = () => {
  return useMutation({
    mutationFn: (data: ChangePasswordRequest) => changePassword(data),
  });
};