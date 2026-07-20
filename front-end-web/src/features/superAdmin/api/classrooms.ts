import { apiClient } from '../../../lib/axios';
import type {
  ClassroomAdminItem,
  CreateClassroomAdminRequest,
  PagedResult,
  SearchClassroomsParams,
  UpdateClassroomAdminRequest,
} from '../types';

export const searchClassrooms = async (
  params: SearchClassroomsParams = {},
): Promise<PagedResult<ClassroomAdminItem>> => {
  const response = await apiClient.get<PagedResult<ClassroomAdminItem>>(
    '/super-admin/classrooms',
    {
      params: {
        search: params.search || undefined,
        teacherId: params.teacherId || undefined,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 20,
      },
    },
  );

  return response.data;
};

export const createClassroom = async (
  data: CreateClassroomAdminRequest,
): Promise<{ id: string }> => {
  const response = await apiClient.post<{ id: string }>(
    '/super-admin/classrooms',
    data,
  );
  return response.data;
};

export const updateClassroom = async (
  id: string,
  data: UpdateClassroomAdminRequest,
): Promise<void> => {
  await apiClient.put(`/super-admin/classrooms/${id}`, data);
};
