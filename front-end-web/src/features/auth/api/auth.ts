import { apiClient } from '../../../lib/axios';
import type {
  LoginRequest,
  LoginResponse,
  RegisterRequest,
  RegistrationRole,
} from '../types';

export const login = async (data: LoginRequest): Promise<LoginResponse> => {
  const response = await apiClient.post<LoginResponse>('/auth/login', data);
  return response.data;
};

export const register = async (data: RegisterRequest): Promise<void> => {
  await apiClient.post('/auth/register', data);
};

export const getRegistrationRoles = async (): Promise<RegistrationRole[]> => {
  const response = await apiClient.get<RegistrationRole[]>('/auth/registration-roles');
  return response.data;
};