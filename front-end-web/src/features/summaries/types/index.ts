// Summary artifacts generated from a live session. Downloadable as PDF or MD
// via a short-lived pre-signed S3 URL fetched on demand — the list never
// returns raw s3 keys.

export type SummaryStatus = 'Generating' | 'Available' | 'Failed';

/** Downloadable formats for a summary artifact. Default is PDF. */
export type SummaryFormat = 'pdf' | 'md';

export interface Summary {
  summaryId: string;
  sessionId: string;
  classroomId: string;
  status: SummaryStatus;
  createdAt: string;
  /** ISO timestamp of when the summary became available, null otherwise. */
  availableAt: string | null;
}

/** Response of the download-url endpoint: a short-lived pre-signed S3 URL. */
export interface DownloadUrlResponse {
  url: string;
  expiresAt: string;
}
