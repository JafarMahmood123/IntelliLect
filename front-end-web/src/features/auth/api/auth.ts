import { apiClient } from '../../../lib/axios';
import type {
  LoginRequest,
  LoginResponse,
  LoginResult,
  RegisterRequest,
  ResetPasswordRequest,
  VerifyTwoFactorRequest,
} from '../types';

export const login = async (data: LoginRequest): Promise<LoginResult> => {
  const response = await apiClient.post<LoginResult>('/auth/login', data);
  return response.data;
};

export const verifyTwoFactor = async (
  data: VerifyTwoFactorRequest,
): Promise<LoginResponse> => {
  const response = await apiClient.post<LoginResponse>('/auth/verify-2fa', data);
  return response.data;
};

export const register = async (data: RegisterRequest): Promise<void> => {
  await apiClient.post('/auth/register', data);
};

export const logout = async (refreshToken: string): Promise<void> => {
  await apiClient.post('/auth/logout', { refreshToken });
};

export const forgotPassword = async (email: string): Promise<void> => {
  await apiClient.post('/auth/forgot-password', { email });
};

export const resetPassword = async (data: ResetPasswordRequest): Promise<void> => {
  await apiClient.post('/auth/reset-password', data);
};