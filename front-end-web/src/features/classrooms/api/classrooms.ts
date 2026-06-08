import { apiClient } from '../../../lib/axios';
import type { Classroom, ClassroomFile, CreateClassroomRequest, CreateSessionRequest, EnrollmentResponse, LearningSession, Session } from '../types';

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

export const deleteFile = async (classroomId: string, fileId: string): Promise<void> => {
  await apiClient.delete(`/classrooms/${classroomId}/files/${fileId}`);
};

export const getClassroomSessions = async (classroomId: string): Promise<Session[]> => {
  const { data } = await apiClient.get<Session[]>(`/classrooms/${classroomId}/sessions`);
  return data;
};

export const createSession = async (classroomId: string, request: CreateSessionRequest): Promise<Session> => {
  const { data } = await apiClient.post<Session>(`/classrooms/${classroomId}/sessions`, request);
  return data;
};

export const getAllClassrooms = async (): Promise<Classroom[]> => {
  const response = await apiClient.get<Classroom[]>('/classrooms'); 
  return response.data;
};

export const enrollInClassroom = async (classroomId: string): Promise<EnrollmentResponse> => {
  const response = await apiClient.post<EnrollmentResponse>(`/classrooms/${classroomId}/members/enroll`);
  return response.data;
};