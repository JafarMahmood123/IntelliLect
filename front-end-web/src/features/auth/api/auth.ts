import { apiClient } from '../../../lib/axios';
import type { LoginRequest, LoginResponse, RegisterRequest } from '../types';

export const login = async (data: LoginRequest): Promise<LoginResponse> => {
  const response = await apiClient.post<LoginResponse>('/auth/login', data);
  return response.data;
};

export const register = async (data: RegisterRequest): Promise<void> => {
  await apiClient.post('/auth/register', data);
};