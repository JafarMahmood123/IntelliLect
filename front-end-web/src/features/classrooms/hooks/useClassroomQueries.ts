import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getTeacherClassrooms, createClassroom, startSession, getSessions, getClassroomFiles, getClassroomById } from '../api/classrooms';
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

export const useClassroomDetails = (id: string) => {
  return useQuery({
    queryKey: classroomKeys.detail(id),
    queryFn: () => getClassroomById(id),
  });
};

export const useClassroomFiles = (classroomId: string) => {
  return useQuery({
    queryKey: [...classroomKeys.detail(classroomId), 'files'],
    queryFn: () => getClassroomFiles(classroomId),
  });
};

export const useClassroomSessions = (classroomId: string) => {
  return useQuery({
    queryKey: [...classroomKeys.detail(classroomId), 'sessions'],
    queryFn: () => getSessions(classroomId),
  });
};

export const useStartSession = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (sessionId: string) => startSession(classroomId, sessionId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: [...classroomKeys.detail(classroomId), 'sessions'] });
    }
  });
};