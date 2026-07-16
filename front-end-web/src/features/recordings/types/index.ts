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
// Stubs for later phases (summaries / documents). Types only — the summaries
// phase fills in the implementation. Kept here so the shared components
// (StatusBadge, SecureDownloadButton, ArtifactList) can be reused as-is.
// ---------------------------------------------------------------------------

export type SummaryStatus = 'Pending' | 'Generating' | 'Done' | 'Failed';

export interface Summary {
  summaryId: string;
  sessionId: string;
  classroomId: string;
  status: SummaryStatus;
  createdAt: string;
  availableAt: string | null;
}

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
