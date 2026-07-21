import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import {
  searchClassroomMembers,
  addClassroomMember,
  removeClassroomMember,
} from '../api/members';
import type { SearchMembersParams } from '../types';

export const useClassroomMembers = (classroomId: string, params: SearchMembersParams) => {
  return useQuery({
    queryKey: ['classroom-members', classroomId, params],
    queryFn: () => searchClassroomMembers(classroomId, params),
    placeholderData: keepPreviousData,
    staleTime: 15_000,
    enabled: !!classroomId,
  });
};

export const useAddClassroomMember = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (studentId: string) => addClassroomMember(classroomId, studentId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['classroom-members', classroomId] });
    },
  });
};

export const useRemoveClassroomMember = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ studentId, reason }: { studentId: string; reason: string }) =>
      removeClassroomMember(classroomId, studentId, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['classroom-members', classroomId] });
    },
  });
};
