import { apiClient } from '../../../lib/axios';
import type { DownloadUrlResponse, Summary, SummaryFormat } from '../types';

export const getSummaries = async (
  classroomId: string,
  sessionId?: string,
): Promise<Summary[]> => {
  const { data } = await apiClient.get<Summary[]>(
    `/classrooms/${classroomId}/summaries`,
    { params: sessionId ? { sessionId } : undefined },
  );
  return data;
};

export const getSummary = async (
  classroomId: string,
  summaryId: string,
): Promise<Summary> => {
  const { data } = await apiClient.get<Summary>(
    `/classrooms/${classroomId}/summaries/${summaryId}`,
  );
  return data;
};

/**
 * Fetches a fresh, short-lived pre-signed S3 URL for a summary in the given
 * format (defaults to PDF). MUST be called on demand (on download click) —
 * never pre-fetched/cached, because the URL expires quickly.
 */
export const getSummaryDownloadUrl = async (
  classroomId: string,
  summaryId: string,
  format: SummaryFormat = 'pdf',
): Promise<DownloadUrlResponse> => {
  const { data } = await apiClient.get<DownloadUrlResponse>(
    `/classrooms/${classroomId}/summaries/${summaryId}/download-url`,
    { params: { format } },
  );
  return data;
};
