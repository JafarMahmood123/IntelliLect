import { apiClient } from '../../../lib/axios';
import type { Classroom, CreateClassroomRequest, UpdateClassroomRequest } from '../types';

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