import { apiClient } from '../../../lib/axios';
import type {
  BulkUserStatusResult,
  PagedResult,
  SearchUsersParams,
  UserDetailResponse,
  UserStatusAction,
  UserSummary,
} from '../types';

export const searchUsers = async (
  params: SearchUsersParams = {},
): Promise<PagedResult<UserSummary>> => {
  const response = await apiClient.get<PagedResult<UserSummary>>(
    '/super-admin/users',
    {
      params: {
        searchTerm: params.searchTerm || undefined,
        role: params.role || undefined,
        status: params.status || undefined,
        createdFrom: params.createdFrom || undefined,
        createdTo: params.createdTo || undefined,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 20,
        sortBy: params.sortBy,
        sortDirection: params.sortDirection,
      },
    },
  );

  return response.data;
};

export const getUserDetail = async (
  userId: string,
): Promise<UserDetailResponse> => {
  const response = await apiClient.get<UserDetailResponse>(
    `/super-admin/users/${userId}`,
  );
  return response.data;
};

export const changeUserStatus = async (
  userId: string,
  action: UserStatusAction,
): Promise<UserSummary> => {
  const response = await apiClient.put<UserSummary>(
    `/super-admin/users/${userId}/status`,
    { action },
  );
  return response.data;
};

/**
 * Applies one action to many accounts in a single request.
 *
 * Resolves rather than rejects when individual accounts failed — the request succeeded, some
 * accounts did not, and a rejection would hide the ones that DID change. Callers must read
 * `results`. It rejects only when the request could not be attempted at all: no ids, more than
 * the server's cap, or an unknown action.
 */
export const bulkChangeUserStatus = async (
  userIds: string[],
  action: UserStatusAction,
): Promise<BulkUserStatusResult> => {
  const response = await apiClient.put<BulkUserStatusResult>('/super-admin/users/status', {
    userIds,
    action,
  });
  return response.data;
};
