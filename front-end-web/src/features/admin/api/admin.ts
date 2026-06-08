import { apiClient } from '../../../lib/axios';
import type { User } from '../../../types';
import type { PagedResult, GetUsersParams, UserStatusPayload } from '../types';

export const getPendingRequests = async (params: GetUsersParams = {}) => {
  const response = await apiClient.get<PagedResult<User>>('/admin/requests', {
    params: {
      page: params.page ?? 1,
      pageSize: params.pageSize ?? 10,
      roleId: params.roleId,
    },
  });
  return response.data;
};

export const getAllUsers = async (params: GetUsersParams = {}) => {
  const response = await apiClient.get<PagedResult<User>>('/admin/users', {
    params: {
      page: params.page ?? 1,
      pageSize: params.pageSize ?? 10,
      roleId: params.roleId,
    },
  });
  return response.data;
};

export const updateUserStatus = async (id: string, status: UserStatusPayload) => {
  await apiClient.put(`/admin/requests/${id}/status`, JSON.stringify(status), {
    headers: {
      'Content-Type': 'application/json',
    },
  });
};
    
export const deactivateUser = async (id: string) => {
  await apiClient.put(`/admin/users/${id}/deactivate`);
};

export const reactivateUser = async (id: string) => {
  await apiClient.put(`/admin/users/${id}/reactivate`);
};