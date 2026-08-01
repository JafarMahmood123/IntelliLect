import { apiClient } from '../../../lib/axios';
import type {
  GeneratedQuestionDraft,
  GeneratedQuizDraft,
  MySessionQuizSummary,
  MyQuizResult,
  QuizDraftRequest,
  QuizLimits,
  QuizResults,
  QuizStudent,
  QuizSubmissionResult,
  QuizTeacher,
  SessionQuizSummary,
  SubmitAnswerResult,
} from '../types';

const base = (classroomId: string) => `/classrooms/${classroomId}`;

export const getQuizLimits = async (classroomId: string): Promise<QuizLimits> => {
  const { data } = await apiClient.get<QuizLimits>(`${base(classroomId)}/quiz-limits`);
  return data;
};

// --- teacher -----------------------------------------------------------------

export const createQuizDraft = async (
  classroomId: string,
  sessionId: string,
  draft: QuizDraftRequest,
): Promise<QuizTeacher> => {
  const { data } = await apiClient.post<QuizTeacher>(
    `${base(classroomId)}/sessions/${sessionId}/quizzes`,
    draft,
  );
  return data;
};

/**
 * Draft a quiz from what the teacher has just been explaining.
 *
 * Returns the same shape as composing one by hand, because that is exactly what it produces: a
 * Draft, in the composer, for the teacher to edit and publish. The model proposes; the teacher
 * still decides what the class is asked.
 */
export const generateQuizDraft = async (
  classroomId: string,
  sessionId: string,
  questionCount: number,
  /** True for a quiz over the whole lesson, read from the session transcript. */
  wholeSession = false,
): Promise<GeneratedQuizDraft> => {
  const { data } = await apiClient.post<GeneratedQuizDraft>(
    `${base(classroomId)}/sessions/${sessionId}/quizzes/generate`,
    { questionCount, wholeSession },
  );
  return data;
};

/**
 * One generated question for the composer to append. Returns content only — the server persists
 * nothing, so generating one and deleting it leaves no abandoned draft behind.
 *
 * `avoid` carries the questions already written, so a second press does not repeat the first.
 */
export const generateQuizQuestion = async (
  classroomId: string,
  sessionId: string,
  avoid: string[],
): Promise<GeneratedQuestionDraft> => {
  const { data } = await apiClient.post<GeneratedQuestionDraft>(
    `${base(classroomId)}/sessions/${sessionId}/quizzes/generate-question`,
    { avoid },
  );
  return data;
};

/** Answers for a question the teacher wrote themselves. The question text is never changed. */
export const generateQuizAnswers = async (
  classroomId: string,
  sessionId: string,
  questionText: string,
): Promise<GeneratedQuestionDraft> => {
  const { data } = await apiClient.post<GeneratedQuestionDraft>(
    `${base(classroomId)}/sessions/${sessionId}/quizzes/generate-answers`,
    { questionText },
  );
  return data;
};

export const updateQuizDraft = async (
  classroomId: string,
  quizId: string,
  draft: QuizDraftRequest,
): Promise<QuizTeacher> => {
  const { data } = await apiClient.put<QuizTeacher>(`${base(classroomId)}/quizzes/${quizId}`, draft);
  return data;
};

export const publishQuiz = async (classroomId: string, quizId: string): Promise<QuizTeacher> => {
  const { data } = await apiClient.post<QuizTeacher>(`${base(classroomId)}/quizzes/${quizId}/publish`);
  return data;
};

export const closeQuiz = async (classroomId: string, quizId: string): Promise<QuizTeacher> => {
  const { data } = await apiClient.post<QuizTeacher>(`${base(classroomId)}/quizzes/${quizId}/close`);
  return data;
};

/**
 * More time on a running quiz. Omitting `studentIds` extends it for the whole class; naming
 * students gives the time only to them, leaving everyone else's clock where it was.
 */
export const extendQuiz = async (
  classroomId: string,
  quizId: string,
  seconds: number,
  studentIds?: string[],
): Promise<QuizTeacher> => {
  const { data } = await apiClient.post<QuizTeacher>(
    `${base(classroomId)}/quizzes/${quizId}/extend`,
    { seconds, studentIds: studentIds ?? [] },
  );
  return data;
};

/** Withdraws the quiz from marking. Answers are kept; they simply stop counting. */
export const cancelQuiz = async (classroomId: string, quizId: string): Promise<QuizTeacher> => {
  const { data } = await apiClient.post<QuizTeacher>(`${base(classroomId)}/quizzes/${quizId}/cancel`);
  return data;
};

export const getQuizForTeacher = async (classroomId: string, quizId: string): Promise<QuizTeacher> => {
  const { data } = await apiClient.get<QuizTeacher>(`${base(classroomId)}/quizzes/${quizId}`);
  return data;
};

export const getQuizResults = async (classroomId: string, quizId: string): Promise<QuizResults> => {
  const { data } = await apiClient.get<QuizResults>(`${base(classroomId)}/quizzes/${quizId}/results`);
  return data;
};

// --- student -----------------------------------------------------------------

/**
 * The session's currently-open quiz, or null. This is what a student who joined after the quiz was
 * published uses — the live broadcast alone would leave them with nothing.
 */
export const getOpenQuizForSession = async (
  classroomId: string,
  sessionId: string,
): Promise<QuizStudent | null> => {
  const { data, status } = await apiClient.get<QuizStudent | ''>(
    `${base(classroomId)}/sessions/${sessionId}/quizzes/open`,
  );
  return status === 204 || !data ? null : (data as QuizStudent);
};

export const getQuizForStudent = async (classroomId: string, quizId: string): Promise<QuizStudent> => {
  const { data } = await apiClient.get<QuizStudent>(
    `${base(classroomId)}/quizzes/${quizId}/student-view`,
  );
  return data;
};

export const submitQuizAnswer = async (
  classroomId: string,
  quizId: string,
  questionId: string,
  optionId: string,
): Promise<SubmitAnswerResult> => {
  const { data } = await apiClient.post<SubmitAnswerResult>(
    `${base(classroomId)}/quizzes/${quizId}/answers`,
    { questionId, optionId },
  );
  return data;
};

/**
 * The student declares they have finished, rather than waiting for the timer. Freezes their
 * answers only; the rest of the class carries on. Idempotent server-side.
 */
export const submitQuiz = async (
  classroomId: string,
  quizId: string,
): Promise<QuizSubmissionResult> => {
  const { data } = await apiClient.post<QuizSubmissionResult>(
    `${base(classroomId)}/quizzes/${quizId}/submit`,
  );
  return data;
};

export const getMyQuizResult = async (classroomId: string, quizId: string): Promise<MyQuizResult> => {
  const { data } = await apiClient.get<MyQuizResult>(`${base(classroomId)}/quizzes/${quizId}/my-result`);
  return data;
};

// --- session-wide summaries ---------------------------------------------------

/** Teacher: everyone's marks for the session. Works live and after the session ends. */
export const getSessionQuizSummary = async (
  classroomId: string,
  sessionId: string,
): Promise<SessionQuizSummary> => {
  const { data } = await apiClient.get<SessionQuizSummary>(
    `${base(classroomId)}/sessions/${sessionId}/quiz-summary`,
  );
  return data;
};

/** Student: their own marks for the session. */
export const getMySessionQuizSummary = async (
  classroomId: string,
  sessionId: string,
): Promise<MySessionQuizSummary> => {
  const { data } = await apiClient.get<MySessionQuizSummary>(
    `${base(classroomId)}/sessions/${sessionId}/my-quiz-summary`,
  );
  return data;
};
