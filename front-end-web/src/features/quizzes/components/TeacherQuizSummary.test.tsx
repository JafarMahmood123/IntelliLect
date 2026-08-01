import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TeacherQuizSummary } from './TeacherQuizSummary';
import { renderWithProviders } from '../../../test/test-utils';
import type { QuestionBreakdown, SessionQuizSummary, StudentScore } from '../types';

vi.mock('../api/quizzes', () => ({
  getQuizLimits: vi.fn(),
  createQuizDraft: vi.fn(),
  generateQuizDraft: vi.fn(),
  generateQuizQuestion: vi.fn(),
  generateQuizAnswers: vi.fn(),
  updateQuizDraft: vi.fn(),
  publishQuiz: vi.fn(),
  closeQuiz: vi.fn(),
  cancelQuiz: vi.fn(),
  getQuizForTeacher: vi.fn(),
  getQuizResults: vi.fn(),
  getOpenQuizForSession: vi.fn(),
  getQuizForStudent: vi.fn(),
  submitQuizAnswer: vi.fn(),
  submitQuiz: vi.fn(),
  getMyQuizResult: vi.fn(),
  getSessionQuizSummary: vi.fn(),
  getMySessionQuizSummary: vi.fn(),
}));

import { getSessionQuizSummary } from '../api/quizzes';

const mockSummary = vi.mocked(getSessionQuizSummary);

const CLASSROOM_ID = 'class-1';
const SESSION_ID = 'session-1';

const question = (overrides: Partial<QuestionBreakdown> = {}): QuestionBreakdown => ({
  questionId: 'q-1',
  quizId: 'quiz-1',
  quizTitle: 'Photosynthesis',
  quizStatus: 'Closed',
  countsTowardsMarks: true,
  order: 0,
  text: 'Which gas do plants take in?',
  points: 5,
  answeredCount: 2,
  correctCount: 1,
  options: [
    { optionId: 'opt-co2', text: 'Carbon dioxide', isCorrect: true, selectedCount: 1 },
    { optionId: 'opt-nitrogen', text: 'Nitrogen', isCorrect: false, selectedCount: 1 },
  ],
  ...overrides,
});

const student = (overrides: Partial<StudentScore> = {}): StudentScore => ({
  studentId: 'student-1',
  studentName: 'Amina',
  score: 5,
  totalPointsAvailable: 5,
  answeredCount: 1,
  correctCount: 1,
  percentage: 100,
  answers: [
    {
      quizId: 'quiz-1',
      questionId: 'q-1',
      selectedOptionId: 'opt-co2',
      isCorrect: true,
      pointsAwarded: 5,
      answeredAtUtc: '2026-01-01T10:00:00Z',
    },
  ],
  ...overrides,
});

const summary = (overrides: Partial<SessionQuizSummary> = {}): SessionQuizSummary => ({
  sessionId: SESSION_ID,
  quizCount: 1,
  countedQuizCount: 1,
  totalPointsAvailable: 5,
  students: [student()],
  questions: [question()],
  ...overrides,
});

const renderPanel = () =>
  renderWithProviders(<TeacherQuizSummary classroomId={CLASSROOM_ID} sessionId={SESSION_ID} />);

describe('TeacherQuizSummary', () => {
  afterEach(() => vi.clearAllMocks());

  it('groups the questions under the quiz they were published in', async () => {
    mockSummary.mockResolvedValue(
      summary({
        quizCount: 2,
        countedQuizCount: 2,
        questions: [
          question(),
          question({
            questionId: 'q-2',
            quizId: 'quiz-2',
            quizTitle: 'Respiration',
            text: 'What does respiration release?',
          }),
        ],
      }),
    );
    renderPanel();

    expect(await screen.findByText('Photosynthesis')).toBeInTheDocument();
    expect(screen.getByText('Respiration')).toBeInTheDocument();
  });

  it("names the right answer next to a student's wrong one", async () => {
    mockSummary.mockResolvedValue(
      summary({
        students: [
          student({
            studentName: 'Bilal',
            score: 0,
            correctCount: 0,
            percentage: 0,
            answers: [
              {
                quizId: 'quiz-1',
                questionId: 'q-1',
                selectedOptionId: 'opt-nitrogen',
                isCorrect: false,
                pointsAwarded: 0,
                answeredAtUtc: '2026-01-01T10:00:00Z',
              },
            ],
          }),
        ],
      }),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: /Bilal/ }));

    // Both the choice and the correct option, so the teacher can see the misconception itself
    // rather than just a zero.
    const drilldown = screen.getByText('Which gas do plants take in?').closest('div')!;
    expect(within(drilldown).getByText('Nitrogen')).toBeInTheDocument();
    expect(within(drilldown).getByText('Carbon dioxide')).toBeInTheDocument();
  });

  it('lists a student who took part without answering anything', async () => {
    // Built from submissions as well as answers — this is the student a teacher most wants to see.
    mockSummary.mockResolvedValue(
      summary({
        students: [
          student({ studentName: 'Sara', score: 0, answeredCount: 0, correctCount: 0, percentage: 0, answers: [] }),
        ],
      }),
    );
    renderPanel();

    const row = await screen.findByRole('button', { name: /Sara/ });
    expect(within(row).getByText('No answers')).toBeInTheDocument();
    expect(row).toBeDisabled();
  });
});
