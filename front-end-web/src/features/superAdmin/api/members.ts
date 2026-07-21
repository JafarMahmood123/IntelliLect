import { apiClient } from '../../../lib/axios';
import type {
  ClassroomMemberItem,
  MemberChangeSummary,
  PagedResult,
  SearchMembersParams,
} from '../types';

// Step 3: paged, searchable list of a classroom's members (teacher + students).
export const searchClassroomMembers = async (
  classroomId: string,
  params: SearchMembersParams = {},
): Promise<PagedResult<ClassroomMemberItem>> => {
  const response = await apiClient.get<PagedResult<ClassroomMemberItem>>(
    `/super-admin/classrooms/${classroomId}/members`,
    {
      params: {
        search: params.search || undefined,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 20,
      },
    },
  );
  return response.data;
};

// Steps 5-6: add a student to the classroom.
export const addClassroomMember = async (
  classroomId: string,
  studentId: string,
): Promise<MemberChangeSummary> => {
  const response = await apiClient.post<MemberChangeSummary>(
    `/super-admin/classrooms/${classroomId}/members`,
    { studentId },
  );
  return response.data;
};

// Steps 5-6: remove a member. The reason is mandatory (4أ).
export const removeClassroomMember = async (
  classroomId: string,
  studentId: string,
  reason: string,
): Promise<MemberChangeSummary> => {
  const response = await apiClient.delete<MemberChangeSummary>(
    `/super-admin/classrooms/${classroomId}/members/${studentId}`,
    { data: { reason } },
  );
  return response.data;
};
