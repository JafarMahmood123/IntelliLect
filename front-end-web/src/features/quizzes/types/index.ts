/** Mirrors the backend QuizStatus enum. */
export type QuizStatus = 'Draft' | 'Open' | 'Closed' | 'Cancelled';

/**
 * Server-owned composer bounds. Fetched rather than hardcoded for the same reason
 * `IMediaSettings` is delivered from the server: a limit duplicated in frontend config is baked
 * into the bundle by Vite and drifts from the API the moment either side changes.
 */
export interface QuizLimits {
  maxQuestionsPerQuiz: number;
  minAnswersPerQuestion: number;
  maxAnswersPerQuestion: number;
  defaultSecondsPerQuestion: number;
  maxQuizDurationSeconds: number;
}

// --- composing ---------------------------------------------------------------

export interface OptionDraft {
  text: string;
  isCorrect: boolean;
}

export interface QuestionDraft {
  text: string;
  points: number;
  timeLimitSeconds: number;
  options: OptionDraft[];
}

export interface QuizDraftRequest {
  title: string;
  questions: QuestionDraft[];
}

/**
 * A generated question shaped for the composer to append directly. Marks and timing are the
 * server's defaults, so it drops straight into `QuestionDraft` with no translation.
 */
export type GeneratedQuestionDraft = QuestionDraft;

// --- teacher view ------------------------------------------------------------

export interface QuizOptionTeacher {
  id: string;
  order: number;
  text: string;
  isCorrect: boolean;
  selectedCount: number;
}

export interface QuizQuestionTeacher {
  id: string;
  order: number;
  text: string;
  points: number;
  timeLimitSeconds: number;
  options: QuizOptionTeacher[];
}

export interface QuizTeacher {
  id: string;
  sessionId: string;
  title: string;
  status: QuizStatus;
  totalPoints: number;
  totalSeconds: number;
  closesAtUtc: string | null;
  serverNowUtc: string;
  respondentCount: number;
  /** How many students have finished, so the teacher can close early. */
  submittedCount: number;
  questions: QuizQuestionTeacher[];
}

// --- student view ------------------------------------------------------------

/** No `isCorrect` — the server's student projection cannot express it. */
export interface QuizOptionStudent {
  id: string;
  order: number;
  text: string;
}

export interface QuizQuestionStudent {
  id: string;
  order: number;
  text: string;
  points: number;
  timeLimitSeconds: number;
  selectedOptionId: string | null;
  options: QuizOptionStudent[];
}

export interface QuizStudent {
  id: string;
  sessionId: string;
  title: string;
  status: QuizStatus;
  totalPoints: number;
  closesAtUtc: string | null;
  /** Pair with `closesAtUtc` to count down against the SERVER's clock, never the device's. */
  serverNowUtc: string;
  /** Set once this student has declared themselves finished; their answers are then frozen. */
  submittedAtUtc: string | null;
  questions: QuizQuestionStudent[];
}

export interface QuizSubmissionResult {
  quizId: string;
  submittedAtUtc: string;
  answeredCount: number;
  questionCount: number;
}

export interface SubmitAnswerResult {
  questionId: string;
  selectedOptionId: string;
  answeredAtUtc: string;
}

// --- results -----------------------------------------------------------------

export interface StudentQuizResult {
  studentId: string;
  score: number;
  totalPoints: number;
  answeredCount: number;
  correctCount: number;
}

export interface QuizResults {
  quizId: string;
  status: QuizStatus;
  totalPoints: number;
  countsTowardsMarks: boolean;
  students: StudentQuizResult[];
}

export interface MyAnswer {
  questionId: string;
  selectedOptionId: string;
  /** Null while the quiz is still open — correctness is withheld until it closes. */
  isCorrect: boolean | null;
  pointsAwarded: number;
}

export interface MyQuizResult {
  quizId: string;
  status: QuizStatus;
  score: number;
  totalPoints: number;
  countsTowardsMarks: boolean;
  answers: MyAnswer[];
}

// --- session-wide summaries ---------------------------------------------------

export interface StudentScore {
  studentId: string;
  studentName: string;
  score: number;
  totalPointsAvailable: number;
  answeredCount: number;
  correctCount: number;
  percentage: number;
}

export interface OptionBreakdown {
  optionId: string;
  text: string;
  isCorrect: boolean;
  selectedCount: number;
}

export interface QuestionBreakdown {
  questionId: string;
  quizId: string;
  quizTitle: string;
  quizStatus: QuizStatus;
  countsTowardsMarks: boolean;
  order: number;
  text: string;
  points: number;
  answeredCount: number;
  correctCount: number;
  options: OptionBreakdown[];
}

export interface SessionQuizSummary {
  sessionId: string;
  quizCount: number;
  countedQuizCount: number;
  totalPointsAvailable: number;
  students: StudentScore[];
  questions: QuestionBreakdown[];
}

export interface MyQuizScore {
  quizId: string;
  title: string;
  status: QuizStatus;
  countsTowardsMarks: boolean;
  score: number;
  totalPoints: number;
  answeredCount: number;
  questionCount: number;
}

export interface MySessionQuizSummary {
  sessionId: string;
  score: number;
  totalPointsAvailable: number;
  percentage: number;
  quizzes: MyQuizScore[];
}
