import { apiClient } from '../../../lib/axios';
import type { Classroom, ClassroomFile, CreateClassroomRequest, CreateSessionRequest, EnrollmentResponse, FileIndexingStatusResponse, LearningSession, Session, SessionEndOutcome, PagedResult, UploadLimits } from '../types';

export const getTeacherClassrooms = async (): Promise<Classroom[]> => {
  const response = await apiClient.get<Classroom[]>('/classrooms/teacher');
  return response.data;
};

export const getEnrolledClassrooms = async (): Promise<Classroom[]> => {
  const response = await apiClient.get<Classroom[]>('/classrooms/enrolled');
  return response.data;
};

export const createClassroom = async (data: CreateClassroomRequest): Promise<{ id: string }> => {
  const response = await apiClient.post<{ id: string }>('/classrooms', data);
  return response.data;
};

export const getClassroomById = async (id: string): Promise<Classroom> => {
  const response = await apiClient.get<Classroom>(`/classrooms/${id}`);
  return response.data;
};

// Files
export const getClassroomFiles = async (classroomId: string): Promise<ClassroomFile[]> => {
  const response = await apiClient.get<ClassroomFile[]>(`/classrooms/${classroomId}/files`);
  return response.data;
};

/**
 * The server's upload bounds, so the picker can refuse a file before spending the upload on it.
 * Readable by any member — the limits are not a secret, and the control needs them before a
 * teacher chooses a file.
 */
export const getUploadLimits = async (classroomId: string): Promise<UploadLimits> => {
  const response = await apiClient.get<UploadLimits>(`/classrooms/${classroomId}/files/upload-limits`);
  return response.data;
};

export const uploadFile = async (classroomId: string, file: File): Promise<ClassroomFile> => {
  const formData = new FormData();
  formData.append('file', file);
  const response = await apiClient.post<ClassroomFile>(`/classrooms/${classroomId}/files`, formData, {
    headers: { 'Content-Type': 'multipart/form-data' }
  });
  return response.data;
};

// Sessions
export const getSessions = async (classroomId: string): Promise<LearningSession[]> => {
  const response = await apiClient.get<LearningSession[]>(`/classrooms/${classroomId}/sessions`);
  return response.data;
};

export const startSession = async (classroomId: string, sessionId: string): Promise<void> => {
  await apiClient.post(`/classrooms/${classroomId}/sessions/${sessionId}/start`);
};

/**
 * Closes a live session: the students are disconnected from the room and the backend starts
 * finalizing the recording and generating the summary/notes. Teacher-only, and idempotent — a
 * session that is already over comes back as `alreadyEnded` rather than an error.
 */
export const endSession = async (
  classroomId: string,
  sessionId: string,
): Promise<SessionEndOutcome> => {
  const { data } = await apiClient.post<SessionEndOutcome>(
    `/classrooms/${classroomId}/sessions/${sessionId}/end`,
  );
  return data;
};

export const deleteFile = async (classroomId: string, fileId: string): Promise<void> => {
  await apiClient.delete(`/classrooms/${classroomId}/files/${fileId}`);
};

// Member-authorized RAG indexing status for a file. The browser never sees any
// internal secret — ClassroomService reads RagService server-side.
export const getFileIndexingStatus = async (
  classroomId: string,
  fileId: string,
): Promise<FileIndexingStatusResponse> => {
  const { data } = await apiClient.get<FileIndexingStatusResponse>(
    `/classrooms/${classroomId}/files/${fileId}/indexing-status`,
  );
  return data;
};

/**
 * Downloads a classroom material file by streaming it through the API/gateway (same origin the
 * app already uses) rather than a direct-to-MinIO link. The request carries the auth header via
 * apiClient; the blob is then saved with the original file name. Called on demand (on click).
 */
export const downloadClassroomFile = async (
  classroomId: string,
  fileId: string,
  fileName: string,
): Promise<void> => {
  const { data } = await apiClient.get<Blob>(
    `/classrooms/${classroomId}/files/${fileId}/download`,
    { responseType: 'blob' },
  );

  const objectUrl = URL.createObjectURL(data);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
};

export const getClassroomSessions = async (classroomId: string): Promise<Session[]> => {
  const { data } = await apiClient.get<Session[]>(`/classrooms/${classroomId}/sessions`);
  return data;
};

export const createSession = async (classroomId: string, request: CreateSessionRequest): Promise<Session> => {
  const { data } = await apiClient.post<Session>(`/classrooms/${classroomId}/sessions`, request);
  return data;
};

// UPDATED: Now accepts pagination parameters
export const getAllClassrooms = async (page = 1, pageSize = 12): Promise<PagedResult<Classroom>> => {
  const response = await apiClient.get<PagedResult<Classroom>>('/classrooms', {
    params: { page, pageSize }
  }); 
  return response.data;
};

export const enrollInClassroom = async (classroomId: string): Promise<EnrollmentResponse> => {
  const response = await apiClient.post<EnrollmentResponse>(`/classrooms/${classroomId}/members/enroll`);
  return response.data;
};