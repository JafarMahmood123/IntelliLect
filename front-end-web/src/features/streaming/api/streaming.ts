import { apiClient } from '../../../lib/axios';
import type { StreamResponse } from '../types';

export const getStreamDetails = async (sessionId: string): Promise<StreamResponse> => {
  const { data } = await apiClient.get<StreamResponse>(`/streams/${sessionId}`);
  return data;
};

export const joinStream = async (sessionId: string): Promise<void> => {
  await apiClient.post(`/streams/${sessionId}/join`);
};

export const leaveStream = async (sessionId: string): Promise<void> => {
  await apiClient.delete(`/streams/${sessionId}/leave`);
};
