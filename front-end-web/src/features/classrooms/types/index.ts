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

export interface LearningSession {
  id: string;
  title: string;
  description: string;
  status: 'Scheduled' | 'Live' | 'Completed' | 'Cancelled';
  scheduledAtUtc: string;
  startedAtUtc?: string;
  classroomId: string;
}

export interface CreateSessionRequest {
  title: string;
  description: string;
  scheduledAtUtc: string;
}