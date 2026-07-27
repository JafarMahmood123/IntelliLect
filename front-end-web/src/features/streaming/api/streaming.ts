import { apiClient } from '../../../lib/axios';
import type { PublishPolicy, StreamResponse } from '../types';

export const getStreamDetails = async (sessionId: string): Promise<StreamResponse> => {
  const { data } = await apiClient.get<StreamResponse>(`/streams/${sessionId}`);
  return data;
};

/** Teacher-only: set whether students may publish audio/video for this live session. */
export const updatePublishPolicy = async (
  sessionId: string,
  policy: PublishPolicy,
): Promise<PublishPolicy> => {
  const { data } = await apiClient.put<PublishPolicy>(`/streams/${sessionId}/publish-policy`, policy);
  return data;
};

export const joinStream = async (sessionId: string): Promise<void> => {
  await apiClient.post(`/streams/${sessionId}/join`);
};

export const leaveStream = async (sessionId: string): Promise<void> => {
  await apiClient.delete(`/streams/${sessionId}/leave`);
};

export const askQuestion = async (sessionId: string, questionText: string): Promise<void> => {
    await apiClient.post(`/streams/${sessionId}/questions`, { questionText });
};

export const answerQuestion = async (sessionId: string, questionId: string, answerText: string): Promise<void> => {
    await apiClient.post(`/streams/${sessionId}/questions/${questionId}/answer`, { answerText });
};

export const getQuestions = async (sessionId: string) => {
    const { data } = await apiClient.get(`/streams/${sessionId}/questions`);
    return data;
};