import { apiClient } from '../../../lib/axios';
import type {
  AdminFileItem,
  BulkReindexResponse,
  FileDeletionResponse,
  FileDetailResult,
  FileListResult,
  KnowledgeStatsResponse,
  SearchFilesParams,
} from '../types';

export type { AdminFileItem };

export const searchFiles = async (
  params: SearchFilesParams = {},
): Promise<FileListResult> => {
  const response = await apiClient.get<FileListResult>('/super-admin/knowledge/files', {
    params: {
      search: params.search || undefined,
      status: params.status || undefined,
      classroomId: params.classroomId || undefined,
      page: params.page ?? 1,
      pageSize: params.pageSize ?? 20,
    },
  });
  return response.data;
};

export const getFileDetail = async (fileId: string): Promise<FileDetailResult> => {
  const response = await apiClient.get<FileDetailResult>(
    `/super-admin/knowledge/files/${fileId}`,
  );
  return response.data;
};

export const getKnowledgeStats = async (
  classroomId?: string,
): Promise<KnowledgeStatsResponse> => {
  const response = await apiClient.get<KnowledgeStatsResponse>('/super-admin/knowledge/stats', {
    params: { classroomId: classroomId || undefined },
  });
  return response.data;
};

export const reindexFile = async (fileId: string, reason: string): Promise<void> => {
  await apiClient.post(`/super-admin/knowledge/files/${fileId}/reindex`, { reason });
};

export const reindexClassroom = async (
  classroomId: string,
  failedOnly: boolean,
  reason: string,
): Promise<BulkReindexResponse> => {
  const response = await apiClient.post<BulkReindexResponse>(
    `/super-admin/knowledge/classrooms/${classroomId}/reindex`,
    { failedOnly, reason },
  );
  return response.data;
};

export const deleteFile = async (
  fileId: string,
  reason: string,
): Promise<FileDeletionResponse> => {
  const response = await apiClient.delete<FileDeletionResponse>(
    `/super-admin/knowledge/files/${fileId}`,
    { data: { reason } },
  );
  return response.data;
};
