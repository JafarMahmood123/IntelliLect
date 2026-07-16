import { apiClient } from '../../../lib/axios';
import type {
  DownloadUrlResponse,
  GeneratedDocument,
  Recording,
  Summary,
} from '../types';

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
 * Fetches a fresh, short-lived pre-signed S3 URL for a recording.
 * MUST be called on demand (on download click) — never pre-fetched/cached,
 * because the URL expires quickly.
 */
export const getRecordingDownloadUrl = async (
  classroomId: string,
  recordingId: string,
): Promise<DownloadUrlResponse> => {
  const { data } = await apiClient.get<DownloadUrlResponse>(
    `/classrooms/${classroomId}/recordings/${recordingId}/download-url`,
  );
  return data;
};

// --- Summaries (stubs — implemented by the summaries phase) -----------------

export type GetSummaries = (
  classroomId: string,
  sessionId?: string,
) => Promise<Summary[]>;

export type GetSummaryDownloadUrl = (
  classroomId: string,
  summaryId: string,
) => Promise<DownloadUrlResponse>;

// --- Documents (stubs — implemented by a later phase) -----------------------

export type GetDocuments = (
  classroomId: string,
  sessionId?: string,
) => Promise<GeneratedDocument[]>;

export type GetDocumentDownloadUrl = (
  classroomId: string,
  documentId: string,
) => Promise<DownloadUrlResponse>;
