import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getTeacherClassrooms, createClassroom } from '../api/classrooms';
import type { CreateClassroomRequest } from '../types';

export const classroomKeys = {
  all: ['classrooms'] as const,
  teacher: () => [...classroomKeys.all, 'teacher'] as const,
  detail: (id: string) => [...classroomKeys.all, 'detail', id] as const,
};

export const useTeacherClassrooms = () => {
  return useQuery({
    queryKey: classroomKeys.teacher(),
    queryFn: getTeacherClassrooms,
  });
};

export const useCreateClassroom = () => {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (data: CreateClassroomRequest) => createClassroom(data),
    onSuccess: () => {
      // Invalidate the list to trigger a refetch after creation
      queryClient.invalidateQueries({ queryKey: classroomKeys.teacher() });
    },
  });
};