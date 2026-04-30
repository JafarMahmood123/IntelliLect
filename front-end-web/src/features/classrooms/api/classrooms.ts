import { apiClient } from '../../../lib/axios';
import type { Classroom, ClassroomFile, CreateClassroomRequest, CreateSessionRequest, LearningSession, UpdateClassroomRequest } from '../types';

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

export const createSession = async (classroomId: string, data: CreateSessionRequest): Promise<{ sessionId: string }> => {
  const response = await apiClient.post<{ sessionId: string }>(`/classrooms/${classroomId}/sessions`, data);
  return response.data;
};

export const startSession = async (classroomId: string, sessionId: string): Promise<void> => {
  await apiClient.post(`/classrooms/${classroomId}/sessions/${sessionId}/start`);
};

export const deleteFile = async (classroomId: string, fileId: string): Promise<void> => {
  await apiClient.delete(`/classrooms/${classroomId}/files/${fileId}`);
};