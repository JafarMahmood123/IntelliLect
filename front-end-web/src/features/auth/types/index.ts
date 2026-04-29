import type { User } from '../../../types';

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  response: User;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  userName: string;
  email: string;
  firstName: string;
  lastName: string;
  roleId: string;
  password: string;
}

export interface RegistrationRole {
  id: string;
  name: string;
}

export interface ResetPasswordRequest {
  email: string;
  token: string;
  newPassword: string;
}