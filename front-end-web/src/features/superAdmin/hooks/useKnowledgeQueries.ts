import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import {
  searchFiles,
  getFileDetail,
  getKnowledgeStats,
  reindexFile,
  reindexClassroom,
  deleteFile,
} from '../api/knowledge';
import type { SearchFilesParams } from '../types';

export const useKnowledgeFiles = (params: SearchFilesParams) => {
  return useQuery({
    queryKey: ['knowledge-files', params],
    queryFn: () => searchFiles(params),
    placeholderData: keepPreviousData,
    staleTime: 10_000,
  });
};

// Lazily fetched only when a file's detail panel opens (enabled by id).
export const useFileDetail = (fileId: string | null) => {
  return useQuery({
    queryKey: ['knowledge-file-detail', fileId],
    queryFn: () => getFileDetail(fileId as string),
    enabled: !!fileId,
    staleTime: 0,
    gcTime: 0,
  });
};

export const useKnowledgeStats = (classroomId: string | undefined, enabled: boolean) => {
  return useQuery({
    queryKey: ['knowledge-stats', classroomId ?? 'all'],
    queryFn: () => getKnowledgeStats(classroomId),
    enabled,
    staleTime: 10_000,
  });
};

export const useReindexFile = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ fileId, reason }: { fileId: string; reason: string }) =>
      reindexFile(fileId, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['knowledge-files'] });
    },
  });
};

export const useReindexClassroom = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      classroomId,
      failedOnly,
      reason,
    }: {
      classroomId: string;
      failedOnly: boolean;
      reason: string;
    }) => reindexClassroom(classroomId, failedOnly, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['knowledge-files'] });
    },
  });
};

export const useDeleteFile = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ fileId, reason }: { fileId: string; reason: string }) =>
      deleteFile(fileId, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['knowledge-files'] });
      await queryClient.invalidateQueries({ queryKey: ['knowledge-stats'] });
    },
  });
};
