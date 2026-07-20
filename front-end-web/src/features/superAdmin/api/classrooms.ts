import { apiClient } from '../../../lib/axios';
import type {
  ClassroomAdminItem,
  ClassroomDeletionImpact,
  ClassroomDeletionSummary,
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

// Step 3: read-only preview of what deleting the classroom will destroy.
export const getClassroomDeletionImpact = async (
  id: string,
): Promise<ClassroomDeletionImpact> => {
  const response = await apiClient.get<ClassroomDeletionImpact>(
    `/super-admin/classrooms/${id}/deletion-impact`,
  );
  return response.data;
};

// Steps 5-6: delete the classroom and everything it owns. The reason is mandatory (4أ).
export const deleteClassroom = async (
  id: string,
  reason: string,
): Promise<ClassroomDeletionSummary> => {
  const response = await apiClient.delete<ClassroomDeletionSummary>(
    `/super-admin/classrooms/${id}`,
    { data: { reason } },
  );
  return response.data;
};
