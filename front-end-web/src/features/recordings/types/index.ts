// Recording artifacts produced from a live session and stored in S3.
// The list endpoint never returns the raw s3_key — downloads go through a
// short-lived pre-signed URL fetched on demand.

export type RecordingStatus = 'Processing' | 'Available' | 'Failed';

export interface Recording {
  recordingId: string;
  sessionId: string;
  classroomId: string;
  status: RecordingStatus;
  durationSeconds: number;
  sizeBytes: number;
  contentType: string;
  createdAt: string;
  /** ISO timestamp of when the recording became available, null while Processing/Failed. */
  availableAt: string | null;
}

/** Response of the download-url endpoint: a short-lived pre-signed S3 URL. */
export interface DownloadUrlResponse {
  url: string;
  expiresAt: string;
}

// ---------------------------------------------------------------------------
// Stub for a later phase (documents). Types only — kept here so the shared
// components (StatusBadge, SecureDownloadButton, ArtifactList) can be reused
// as-is. Summaries are implemented in the `summaries` feature.
// ---------------------------------------------------------------------------

export type DocumentStatus = 'Pending' | 'Generating' | 'Done' | 'Failed';

export interface GeneratedDocument {
  documentId: string;
  sessionId: string;
  classroomId: string;
  status: DocumentStatus;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  availableAt: string | null;
}
