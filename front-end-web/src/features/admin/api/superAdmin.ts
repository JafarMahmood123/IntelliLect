import { apiClient } from '../../../lib/axios';
import type { AdminQueryResult, CreateAdminRequest, PagedResult } from '../types';

export const getAdmins = async (page = 1, pageSize = 10) => {
  const response = await apiClient.get<PagedResult<AdminQueryResult>>('/super-admin/admins', {
    params: { page, pageSize }
  });
  return response.data;
};

export const createAdmin = async (data: CreateAdminRequest) => {
  const response = await apiClient.post('/super-admin/admins', data);
  return response.data;
};

export const toggleAdminStatus = async (adminId: string, currentStatus: string) => {
  const endpoint = currentStatus === 'Active' ? 'deactivate' : 'reactivate';
  await apiClient.put(`/super-admin/admins/${adminId}/${endpoint}`);
};