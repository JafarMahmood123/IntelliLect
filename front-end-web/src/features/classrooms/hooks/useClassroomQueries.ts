import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getTeacherClassrooms, createClassroom, startSession, getClassroomFiles, getClassroomById, uploadFile, deleteFile, createSession, getClassroomSessions, getEnrolledClassrooms, getAllClassrooms, enrollInClassroom, getFileIndexingStatus } from '../api/classrooms';
import type { CreateClassroomRequest, CreateSessionRequest, FileIndexingStatus } from '../types';

export const classroomKeys = {
  all: ['classrooms'] as const,
  teacher: () => [...classroomKeys.all, 'teacher'] as const,
  enrolled: () => [...classroomKeys.all, 'enrolled'] as const,
  discovery: (page: number) => [...classroomKeys.all, 'discovery', page] as const,
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

export const INDEXING_POLL_INTERVAL_MS = 5000;
export const isTerminalIndexingStatus = (status?: FileIndexingStatus) =>
  status === 'Done' || status === 'Failed';

// Reads a file's RAG indexing status, polling every 5s while it is still
// Pending/Processing and stopping once it reaches a terminal state.
export const useFileIndexingStatus = (classroomId: string, fileId: string) => {
  return useQuery({
    queryKey: [...classroomKeys.detail(classroomId), 'files', fileId, 'indexing-status'],
    queryFn: () => getFileIndexingStatus(classroomId, fileId),
    enabled: Boolean(classroomId && fileId),
    refetchInterval: (query) =>
      isTerminalIndexingStatus(query.state.data?.status)
        ? false
        : INDEXING_POLL_INTERVAL_MS,
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

export const useEnrolledClassrooms = () => {
  return useQuery({
    queryKey: classroomKeys.enrolled(),
    queryFn: getEnrolledClassrooms,
  });
};

// UPDATED: Now accepts page and uses a dynamic queryKey
export const useDiscoveryClassrooms = (page: number) => {
  return useQuery({
    queryKey: classroomKeys.discovery(page),
    queryFn: () => getAllClassrooms(page),
  });
};

export const useEnrollClassroom = () => {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (classroomId: string) => enrollInClassroom(classroomId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: classroomKeys.enrolled() });
      queryClient.invalidateQueries({ queryKey: classroomKeys.all }); // Invalidate discovery lists too
    },
  });
};