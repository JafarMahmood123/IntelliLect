import { apiClient } from '../../../lib/axios';
import type { QaAnswerResponse } from '../types';

// Asks a question about a classroom's material. The classroomId comes from the
// caller's context (route), and the server derives retrieval scope from membership
// — the browser never sends an internal secret and cannot target another classroom.
export const askClassroomQuestion = async (
  classroomId: string,
  question: string,
): Promise<QaAnswerResponse> => {
  const { data } = await apiClient.post<QaAnswerResponse>(
    `/classrooms/${classroomId}/qa/answer`,
    { question },
  );
  return data;
};
