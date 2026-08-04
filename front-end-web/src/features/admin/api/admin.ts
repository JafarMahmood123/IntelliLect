import { apiClient } from '../../../lib/axios';
import type { User } from '../../../types';
import type {
  PagedResult,
  GetUsersParams,
  UserStatusPayload,
  UserStatusAction,
  BulkUserStatusResult,
} from '../types';

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
    
/**
 * Applies one action to many accounts in a single request.
 *
 * Returns 200 even when individual accounts failed — read `failed`/`results`, never assume the
 * whole batch took. A non-2xx here means the request could not be attempted at all (no ids, too
 * many ids, unknown action).
 */
export const bulkUpdateUserStatus = async (
  userIds: string[],
  action: UserStatusAction,
): Promise<BulkUserStatusResult> => {
  const { data } = await apiClient.put<BulkUserStatusResult>('/admin/requests/status', {
    userIds,
    action,
  });
  return data;
};

export const deactivateUser = async (id: string) => {
  await apiClient.put(`/admin/users/${id}/deactivate`);
};

export const reactivateUser = async (id: string) => {
  await apiClient.put(`/admin/users/${id}/reactivate`);
};