import { apiClient } from '../../../lib/axios';
import type { User } from '../../../types';
import type { ChangePasswordRequest, UpdateCurrentUserRequest } from '../types';

export const getCurrentUser = async (): Promise<User> => {
  const response = await apiClient.get<User>('/users/me');
  return response.data;
};

export const updateCurrentUser = async (
  data: UpdateCurrentUserRequest,
): Promise<void> => {
  await apiClient.put('/users/me', data);
};

export const changePassword = async (
  data: ChangePasswordRequest,
): Promise<void> => {
  await apiClient.post('/users/change-password', data);
};