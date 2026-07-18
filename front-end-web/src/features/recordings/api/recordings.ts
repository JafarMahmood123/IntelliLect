import { apiClient } from '../../../lib/axios';
import { filenameFromContentDisposition, triggerBlobDownload } from '../../../utils/download';
import type { DownloadUrlResponse, GeneratedDocument, Recording } from '../types';

// --- Recordings -------------------------------------------------------------

export const getRecordings = async (
  classroomId: string,
  sessionId?: string,
): Promise<Recording[]> => {
  const { data } = await apiClient.get<Recording[]>(
    `/classrooms/${classroomId}/recordings`,
    { params: sessionId ? { sessionId } : undefined },
  );
  return data;
};

export const getRecording = async (
  classroomId: string,
  recordingId: string,
): Promise<Recording> => {
  const { data } = await apiClient.get<Recording>(
    `/classrooms/${classroomId}/recordings/${recordingId}`,
  );
  return data;
};

/**
 * Downloads a recording by streaming it through the API/gateway (auth header + blob) rather than a
 * direct-to-MinIO link, so the browser stays on the app origin. Called on demand (on click).
 */
export const downloadRecording = async (
  classroomId: string,
  recordingId: string,
): Promise<void> => {
  const response = await apiClient.get<Blob>(
    `/classrooms/${classroomId}/recordings/${recordingId}/download`,
    { responseType: 'blob' },
  );
  const fileName =
    filenameFromContentDisposition(response.headers['content-disposition']) ??
    `recording-${recordingId}.mp4`;
  triggerBlobDownload(response.data, fileName);
};

// --- Documents (stubs — implemented by a later phase) -----------------------

export type GetDocuments = (
  classroomId: string,
  sessionId?: string,
) => Promise<GeneratedDocument[]>;

export type GetDocumentDownloadUrl = (
  classroomId: string,
  documentId: string,
) => Promise<DownloadUrlResponse>;
