import type { User } from '../../../types';

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  response: User;
}

// Returned by /auth/login when the account (a super admin) must complete a second
// factor. It deliberately carries no tokens — the code must be verified first.
export interface TwoFactorRequiredResponse {
  requiresTwoFactor: true;
  email: string;
  message: string;
}

export type LoginResult = LoginResponse | TwoFactorRequiredResponse;

export const isTwoFactorRequired = (
  result: LoginResult,
): result is TwoFactorRequiredResponse =>
  'requiresTwoFactor' in result && result.requiresTwoFactor === true;

export interface VerifyTwoFactorRequest {
  email: string;
  code: string;
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

export interface ResetPasswordRequest {
  email: string;
  token: string;
  newPassword: string;
}