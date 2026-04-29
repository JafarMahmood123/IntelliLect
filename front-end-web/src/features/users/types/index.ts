export interface UpdateCurrentUserRequest {
  firstName: string;
  lastName: string;
  userName: string;
  bio?: string | null;
  version: string;
}

export interface ChangePasswordRequest {
  oldPassword: string;
  newPassword: string;
}