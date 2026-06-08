import { apiClient } from '../../../lib/axios';
import type { RegistrationRole } from '../types';

export const getRegistrationRoles = async (): Promise<RegistrationRole[]> => {
  const response = await apiClient.get<RegistrationRole[]>('/auth/registration-roles');
  return response.data;
};