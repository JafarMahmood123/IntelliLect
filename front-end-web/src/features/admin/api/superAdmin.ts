import { apiClient } from '../../../lib/axios';
import type {
  AdminQueryResult,
  CreateAdminRequest,
  GetAdminsParams,
  GroupedAdminsResponse,
  PagedResult,
  SearchAdminsParams,
} from '../types';

export const getAdmins = async (
  params: GetAdminsParams = {},
) => {
  const response = await apiClient.get<PagedResult<AdminQueryResult>>(
    '/super-admin/admins',
    {
      params: {
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 10,
        sortBy: params.sortBy,
        sortDirection: params.sortDirection,
      },
    },
  );

  return response.data;
};

export const getGroupedAdmins = async (
  params: Omit<GetAdminsParams, 'groupBy'> = {},
) => {
  const response = await apiClient.get<GroupedAdminsResponse>(
    '/super-admin/admins',
    {
      params: {
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 10,
        sortBy: params.sortBy,
        sortDirection: params.sortDirection,
        groupBy: 'status',
      },
    },
  );

  return response.data;
};

export const searchAdmins = async (
  params: SearchAdminsParams = {},
) => {
  const response = await apiClient.get<PagedResult<AdminQueryResult>>(
    '/super-admin/admins/search',
    {
      params: {
        userName: params.userName,
        email: params.email,
        firstName: params.firstName,
        lastName: params.lastName,
        status: params.status,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 10,
        sortBy: params.sortBy,
        sortDirection: params.sortDirection,
      },
    },
  );

  return response.data;
};

export const createAdmin = async (data: CreateAdminRequest) => {
  const response = await apiClient.post('/super-admin/admins', data);
  return response.data;
};

export const toggleAdminStatus = async (
  adminId: string,
  currentStatus: string,
) => {
  const endpoint = currentStatus === 'Active' ? 'deactivate' : 'reactivate';
  await apiClient.put(`/super-admin/admins/${adminId}/${endpoint}`);
};