import { apiClient } from '../../../lib/axios';
import type {
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
