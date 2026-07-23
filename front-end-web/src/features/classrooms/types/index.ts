export interface Classroom {
  id: string;
  name: string;
  description: string;
  teacherId: string;
  createdAtUtc: string;
  fileCount: number;
  studentCount: number;
}

export interface CreateClassroomRequest {
  name: string;
  description: string;
}

export interface UpdateClassroomRequest {
  name: string;
  description: string;
}

export interface ClassroomFile {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  s3Key: string;
}

// RAG indexing lifecycle for an uploaded classroom file (read via a
// member-authorized ClassroomService endpoint — no internal secret in the browser).
export type FileIndexingStatus = 'Pending' | 'Processing' | 'Done' | 'Failed';

export interface FileIndexingStatusResponse {
  fileId: string;
  status: FileIndexingStatus;
}

export interface LearningSession {
  id: string;
  title: string;
  description: string;
  status: "Scheduled" | "Live" | "Ended" | "Completed" | "Cancelled";
  scheduledAtUtc: string;
  startedAtUtc?: string;
  classroomId: string;
  participationMode: number; // Added
}

export interface CreateSessionRequest {
  title: string;
  description: string;
  scheduledAtUtc: string;
  participationMode: number; // Added
}

export interface MemberResponse {
  studentId: string;
  fullName: string;
  joinedAtUtc: string;
}

export interface EnrollmentResponse {
  message: string;
}

export type SessionStatus = "Scheduled" | "Live" | "Ended" | 0 | 1 | 2;

export interface Session {
  id: string;
  classroomId: string;
  title: string;
  description: string;
  scheduledAtUtc: string;
  status: SessionStatus;
  startedAtUtc?: string;
  endedAtUtc?: string;
  participationMode: number; // Added
}

/**
 * Result of a teacher ending a session. `status`/`endedAtUtc` are authoritative; the two flags
 * report the best-effort teardown, so the UI can say "the session ended, but its summary could
 * not be started" instead of pretending everything worked.
 */
export interface SessionEndOutcome {
  sessionId: string;
  status: SessionStatus;
  alreadyEnded: boolean;
  streamEnded: boolean;
  summaryTriggered: boolean;
  endedAtUtc?: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
}
