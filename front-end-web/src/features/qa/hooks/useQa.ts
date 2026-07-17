import { useMutation } from '@tanstack/react-query';
import { askClassroomQuestion } from '../api/qa';
import type { QaAnswerResponse } from '../types';

/**
 * Mutation for asking a classroom question. The classroomId is bound from context;
 * only the question varies per call, so a user can never target another classroom.
 */
export const useAskQuestion = (classroomId: string) => {
  return useMutation<QaAnswerResponse, unknown, string>({
    mutationFn: (question: string) => askClassroomQuestion(classroomId, question),
  });
};
