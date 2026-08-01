import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  cancelQuiz,
  closeQuiz,
  createQuizDraft,
  extendQuiz,
  generateQuizAnswers,
  generateQuizDraft,
  generateQuizQuestion,
  getMyQuizResult,
  getMySessionQuizSummary,
  getOpenQuizForSession,
  getQuizForStudent,
  getQuizForTeacher,
  getQuizLimits,
  getQuizResults,
  getSessionQuizSummary,
  publishQuiz,
  submitQuiz,
  submitQuizAnswer,
  updateQuizDraft,
} from '../api/quizzes';
import type { QuizDraftRequest } from '../types';

export const quizKeys = {
  all: ['quizzes'] as const,
  limits: (classroomId: string) => [...quizKeys.all, 'limits', classroomId] as const,
  detail: (quizId: string) => [...quizKeys.all, 'detail', quizId] as const,
  studentView: (quizId: string) => [...quizKeys.all, 'student', quizId] as const,
  openForSession: (sessionId: string) => [...quizKeys.all, 'open', sessionId] as const,
  results: (quizId: string) => [...quizKeys.all, 'results', quizId] as const,
  myResult: (quizId: string) => [...quizKeys.all, 'my-result', quizId] as const,
  sessionSummary: (sessionId: string) => [...quizKeys.all, 'session-summary', sessionId] as const,
  mySessionSummary: (sessionId: string) => [...quizKeys.all, 'my-session-summary', sessionId] as const,
};

/** Teacher's session-wide marks. Refetched on every quiz state change so the tally stays live. */
export const useSessionQuizSummary = (classroomId: string, sessionId: string) =>
  useQuery({
    queryKey: quizKeys.sessionSummary(sessionId),
    queryFn: () => getSessionQuizSummary(classroomId, sessionId),
    enabled: Boolean(classroomId && sessionId),
  });

export const useMySessionQuizSummary = (classroomId: string, sessionId: string) =>
  useQuery({
    queryKey: quizKeys.mySessionSummary(sessionId),
    queryFn: () => getMySessionQuizSummary(classroomId, sessionId),
    enabled: Boolean(classroomId && sessionId),
  });

/** Composer bounds. Effectively static for a deployment, so it is cached indefinitely. */
export const useQuizLimits = (classroomId: string) =>
  useQuery({
    queryKey: quizKeys.limits(classroomId),
    queryFn: () => getQuizLimits(classroomId),
    enabled: Boolean(classroomId),
    staleTime: Infinity,
  });

export const useOpenQuiz = (classroomId: string, sessionId: string) =>
  useQuery({
    queryKey: quizKeys.openForSession(sessionId),
    queryFn: () => getOpenQuizForSession(classroomId, sessionId),
    enabled: Boolean(classroomId && sessionId),
  });

export const useStudentQuiz = (classroomId: string, quizId: string | undefined) =>
  useQuery({
    queryKey: quizKeys.studentView(quizId ?? ''),
    queryFn: () => getQuizForStudent(classroomId, quizId!),
    enabled: Boolean(classroomId && quizId),
  });

export const useTeacherQuiz = (classroomId: string, quizId: string | undefined) =>
  useQuery({
    queryKey: quizKeys.detail(quizId ?? ''),
    queryFn: () => getQuizForTeacher(classroomId, quizId!),
    enabled: Boolean(classroomId && quizId),
  });

export const useQuizResults = (classroomId: string, quizId: string | undefined) =>
  useQuery({
    queryKey: quizKeys.results(quizId ?? ''),
    queryFn: () => getQuizResults(classroomId, quizId!),
    enabled: Boolean(classroomId && quizId),
  });

export const useMyQuizResult = (classroomId: string, quizId: string | undefined) =>
  useQuery({
    queryKey: quizKeys.myResult(quizId ?? ''),
    queryFn: () => getMyQuizResult(classroomId, quizId!),
    enabled: Boolean(classroomId && quizId),
  });

export const useCreateQuizDraft = (classroomId: string, sessionId: string) =>
  useMutation({
    mutationFn: (draft: QuizDraftRequest) => createQuizDraft(classroomId, sessionId, draft),
  });

/**
 * Generation runs on the server against the session transcript, so it can take several seconds —
 * the caller is expected to show that it is working rather than leave the teacher guessing.
 */
export const useGenerateQuizDraft = (classroomId: string, sessionId: string) =>
  useMutation({
    mutationFn: ({
      questionCount,
      wholeSession = false,
    }: {
      questionCount: number;
      wholeSession?: boolean;
    }) => generateQuizDraft(classroomId, sessionId, questionCount, wholeSession),
  });

/** One question appended to the composer. Nothing is persisted server-side. */
export const useGenerateQuizQuestion = (classroomId: string, sessionId: string) =>
  useMutation({
    mutationFn: (avoid: string[]) => generateQuizQuestion(classroomId, sessionId, avoid),
  });

/** Answers for a question the teacher wrote. Also unpersisted. */
export const useGenerateQuizAnswers = (classroomId: string, sessionId: string) =>
  useMutation({
    mutationFn: (questionText: string) =>
      generateQuizAnswers(classroomId, sessionId, questionText),
  });

/**
 * The quiz id travels with the CALL, not the hook, because the draft being edited is only known
 * once one exists — created by the teacher's first save, or by generation.
 */
export const useUpdateQuizDraft = (classroomId: string) =>
  useMutation({
    mutationFn: ({ quizId, draft }: { quizId: string; draft: QuizDraftRequest }) =>
      updateQuizDraft(classroomId, quizId, draft),
  });

/**
 * Publish/close/cancel all land on the same quiz, so they share one invalidation. The open-quiz
 * key is invalidated too: publishing creates one for the session and closing removes it.
 */
const useQuizLifecycleMutation = (
  classroomId: string,
  sessionId: string,
  action: (classroomId: string, quizId: string) => Promise<unknown>,
) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (quizId: string) => action(classroomId, quizId),
    onSuccess: (_data, quizId) => {
      queryClient.invalidateQueries({ queryKey: quizKeys.detail(quizId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
    },
  });
};

export const usePublishQuiz = (classroomId: string, sessionId: string) =>
  useQuizLifecycleMutation(classroomId, sessionId, publishQuiz);

export const useCloseQuiz = (classroomId: string, sessionId: string) =>
  useQuizLifecycleMutation(classroomId, sessionId, closeQuiz);

export const useCancelQuiz = (classroomId: string, sessionId: string) =>
  useQuizLifecycleMutation(classroomId, sessionId, cancelQuiz);

/**
 * More time on a running quiz. Invalidates the same keys as the lifecycle actions, because a new
 * deadline changes what every client should be counting down to.
 */
export const useExtendQuiz = (classroomId: string, sessionId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({
      quizId,
      seconds,
      studentIds,
    }: {
      quizId: string;
      seconds: number;
      studentIds?: string[];
    }) => extendQuiz(classroomId, quizId, seconds, studentIds),
    onSuccess: (_data, { quizId }) => {
      queryClient.invalidateQueries({ queryKey: quizKeys.detail(quizId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.studentView(quizId) });
    },
  });
};

/**
 * Finishing early. Invalidates the open-quiz read so the panel flips to its submitted state from
 * the server's answer rather than a local guess.
 */
export const useSubmitQuiz = (classroomId: string, quizId: string, sessionId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => submitQuiz(classroomId, quizId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.studentView(quizId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.mySessionSummary(sessionId) });
    },
  });
};

export const useSubmitQuizAnswer = (classroomId: string, quizId: string) =>
  useMutation({
    mutationFn: ({ questionId, optionId }: { questionId: string; optionId: string }) =>
      submitQuizAnswer(classroomId, quizId, questionId, optionId),
  });
