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
 * A point where the course material contradicted what the teacher said out loud.
 *
 * The assistant writes the answer key from the MATERIAL — a class must never be marked wrong for
 * having listened — and reports the disagreement here so the teacher sees it before publishing.
 */
export interface QuizCorrection {
  taught: string;
  corrected: string;
}

/**
 * A generated question shaped for the composer to append directly. Marks and timing are the
 * server's defaults, so it drops straight into `QuestionDraft` with no translation.
 */
export interface GeneratedQuestionDraft extends QuestionDraft {
  corrections: QuizCorrection[];
}

/** A generated draft, and what the assistant had to correct to write it. */
export interface GeneratedQuizDraft {
  quiz: QuizTeacher;
  corrections: QuizCorrection[];
}

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
  /** Who is taking part, so time can be given to a named student rather than to the room. */
  respondents: QuizRespondent[];
  questions: QuizQuestionTeacher[];
}

export interface QuizRespondent {
  studentId: string;
  studentName: string;
  answeredCount: number;
  hasSubmitted: boolean;
  /** Their own deadline — later than the quiz's when they have been given extra time. */
  closesAtUtc: string | null;
  hasExtraTime: boolean;
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

export interface StudentAnswer {
  quizId: string;
  questionId: string;
  selectedOptionId: string;
  isCorrect: boolean;
  pointsAwarded: number;
  answeredAtUtc: string;
}

export interface StudentScore {
  studentId: string;
  studentName: string;
  score: number;
  totalPointsAvailable: number;
  answeredCount: number;
  correctCount: number;
  percentage: number;
  /** Ids only — question and option text live once in `SessionQuizSummary.questions`. */
  answers: StudentAnswer[];
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

export interface MyOptionReview {
  optionId: string;
  order: number;
  text: string;
  isCorrect: boolean;
}

export interface MyQuestionReview {
  questionId: string;
  order: number;
  text: string;
  points: number;
  /** Null if this one was skipped. */
  selectedOptionId: string | null;
  /** Null means UNANSWERED — a withheld review carries no questions at all. */
  isCorrect: boolean | null;
  pointsAwarded: number;
  options: MyOptionReview[];
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
  /** Empty while the quiz is open — the review names the correct option. */
  questions: MyQuestionReview[];
}

// --- classroom-wide tracking ---------------------------------------------------

export interface StudentTracking {
  studentId: string;
  studentName: string;
  /**
   * Position in the class, best first. Standard competition ranking, so tied students share a
   * position and the next one skips it — never use the array index for this, which would show a
   * difference between two students on identical marks.
   */
  rank: number;
  quizzesTaken: number;
  quizCount: number;
  answeredCount: number;
  correctCount: number;
  score: number;
  totalPointsAvailable: number;
  percentage: number;
  sessionsTakenPart: number;
  sessionsWithQuizzesCount: number;
}

export interface SessionTracking {
  sessionId: string;
  title: string;
  scheduledAtUtc: string;
  startedAtUtc: string | null;
  quizCount: number;
  totalPoints: number;
  participantCount: number;
  averagePercentage: number;
}

export interface ClassroomQuizTracking {
  classroomId: string;
  enrolledStudentCount: number;
  activeStudentCount: number;
  sessionCount: number;
  sessionsWithQuizzesCount: number;
  quizCount: number;
  totalPointsAvailable: number;
  classAveragePercentage: number;
  students: StudentTracking[];
  sessions: SessionTracking[];
}

export interface MySessionTracking {
  sessionId: string;
  title: string;
  scheduledAtUtc: string;
  startedAtUtc: string | null;
  score: number;
  totalPoints: number;
  percentage: number;
  quizzesTaken: number;
  quizCount: number;
}

export interface MyClassroomQuizTracking {
  classroomId: string;
  /** This student's own position, or null when they have taken no graded quiz yet. */
  rank: number | null;
  /** How many students the rank is out of. The only other thing said about the class. */
  rankedStudentCount: number;
  score: number;
  totalPointsAvailable: number;
  percentage: number;
  quizzesTaken: number;
  quizCount: number;
  sessionsTakenPart: number;
  sessionsWithQuizzesCount: number;
  /** One number about everyone else, so a score has something to mean. Names nobody. */
  classAveragePercentage: number;
  sessions: MySessionTracking[];
}

export interface MySessionQuizSummary {
  sessionId: string;
  score: number;
  totalPointsAvailable: number;
  percentage: number;
  quizzes: MyQuizScore[];
}
