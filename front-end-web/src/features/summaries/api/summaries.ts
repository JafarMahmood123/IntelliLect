import { apiClient } from '../../../lib/axios';
import { filenameFromContentDisposition, triggerBlobDownload } from '../../../utils/download';
import type { Summary, SummaryFormat } from '../types';

export const getSummaries = async (
  classroomId: string,
  sessionId?: string,
): Promise<Summary[]> => {
  // The endpoint returns a paged result ({ items, totalCount, ... }); tolerate a bare array too.
  const { data } = await apiClient.get<{ items?: Summary[] } | Summary[]>(
    `/classrooms/${classroomId}/summaries`,
    { params: sessionId ? { sessionId } : undefined },
  );
  return Array.isArray(data) ? data : (data.items ?? []);
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
 * Fetches a summary's Markdown as text through the API/gateway (auth-guarded) for inline preview —
 * no direct-to-MinIO link, so no browser HTTPS-upgrade issue.
 */
export const fetchSummaryMarkdownText = async (
  classroomId: string,
  summaryId: string,
): Promise<string> => {
  const { data } = await apiClient.get<string>(
    `/classrooms/${classroomId}/summaries/${summaryId}/download`,
    { params: { format: 'md' }, responseType: 'text' },
  );
  return data;
};

/**
 * Downloads a summary artifact (PDF default, or Markdown) by streaming it through the API/gateway
 * (auth header + blob) rather than a direct-to-MinIO link. Called on demand (on click).
 */
export const downloadSummary = async (
  classroomId: string,
  summaryId: string,
  format: SummaryFormat = 'pdf',
): Promise<void> => {
  const response = await apiClient.get<Blob>(
    `/classrooms/${classroomId}/summaries/${summaryId}/download`,
    { params: { format }, responseType: 'blob' },
  );
  const fileName =
    filenameFromContentDisposition(response.headers['content-disposition']) ??
    `summary-${summaryId}.${format}`;
  triggerBlobDownload(response.data, fileName);
};

/**
 * Re-requests a FAILED summary. Teacher-only, and the server refuses anything other than a failed
 * summary with a 409 — an Available summary would be overwritten in place, since the storage keys
 * are derived from the session id.
 *
 * Returns the summary back in `Generating`; the work itself happens asynchronously, so the list
 * poll is what eventually shows the outcome.
 */
export const regenerateSummary = async (
  classroomId: string,
  summaryId: string,
): Promise<Summary> => {
  const { data } = await apiClient.post<Summary>(
    `/classrooms/${classroomId}/summaries/${summaryId}/regenerate`,
  );
  return data;
};
