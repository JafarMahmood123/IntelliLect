import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getTeacherClassrooms, createClassroom, startSession, getClassroomFiles, getClassroomById, uploadFile, deleteFile, createSession, getClassroomSessions, getEnrolledClassrooms, getAllClassrooms, enrollInClassroom } from '../api/classrooms';
import type { CreateClassroomRequest, CreateSessionRequest } from '../types';

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

export const useStartSession = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (sessionId: string) => startSession(classroomId, sessionId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: [...classroomKeys.detail(classroomId), 'sessions'] });
    }
  });
};

export const useUploadClassroomFile = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (file: File) => uploadFile(classroomId, file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: [...classroomKeys.detail(classroomId), 'files'] });
    }
  });
};

export const useDeleteClassroomFile = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (fileId: string) => deleteFile(classroomId, fileId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: [...classroomKeys.detail(classroomId), 'files'] });
    }
  });
};

export const useClassroomSessions = (classroomId: string) => {
  return useQuery({
    queryKey: [...classroomKeys.detail(classroomId), 'sessions'],
    queryFn: () => getClassroomSessions(classroomId),
  });
};

export const useCreateSession = (classroomId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateSessionRequest) => createSession(classroomId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: [...classroomKeys.detail(classroomId), 'sessions'] });
    },
  });
};
  
export const classroomKeys = {
  all: ['classrooms'] as const,
  teacher: () => [...classroomKeys.all, 'teacher'] as const,
  enrolled: () => [...classroomKeys.all, 'enrolled'] as const,
  discovery: () => [...classroomKeys.all, 'discovery'] as const,
  detail: (id: string) => [...classroomKeys.all, 'detail', id] as const,
};

export const useEnrolledClassrooms = () => {
  return useQuery({
    queryKey: classroomKeys.enrolled(),
    queryFn: getEnrolledClassrooms,
  });
};

export const useDiscoveryClassrooms = () => {
  return useQuery({
    queryKey: classroomKeys.discovery(),
    queryFn: getAllClassrooms,
  });
};

export const useEnrollClassroom = () => {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (classroomId: string) => enrollInClassroom(classroomId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: classroomKeys.enrolled() });
      queryClient.invalidateQueries({ queryKey: classroomKeys.discovery() });
    },
  });
};