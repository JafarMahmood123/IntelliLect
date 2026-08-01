import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { StudentQuizSummary } from './StudentQuizSummary';
import { renderWithProviders } from '../../../test/test-utils';
import type { MySessionQuizSummary, MyQuizScore } from '../types';

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

import { getMySessionQuizSummary } from '../api/quizzes';

const mockSummary = vi.mocked(getMySessionQuizSummary);

const CLASSROOM_ID = 'class-1';
const SESSION_ID = 'session-1';

const closedQuiz = (overrides: Partial<MyQuizScore> = {}): MyQuizScore => ({
  quizId: 'quiz-1',
  title: 'Photosynthesis',
  status: 'Closed',
  countsTowardsMarks: true,
  score: 0,
  totalPoints: 5,
  answeredCount: 1,
  questionCount: 1,
  questions: [
    {
      questionId: 'q-1',
      order: 0,
      text: 'Which gas do plants take in?',
      points: 5,
      selectedOptionId: 'opt-nitrogen',
      isCorrect: false,
      pointsAwarded: 0,
      options: [
        { optionId: 'opt-co2', order: 0, text: 'Carbon dioxide', isCorrect: true },
        { optionId: 'opt-nitrogen', order: 1, text: 'Nitrogen', isCorrect: false },
      ],
    },
  ],
  ...overrides,
});

const summary = (quizzes: MyQuizScore[]): MySessionQuizSummary => ({
  sessionId: SESSION_ID,
  score: quizzes.reduce((total, q) => total + q.score, 0),
  totalPointsAvailable: quizzes.reduce((total, q) => total + q.totalPoints, 0),
  percentage: 0,
  quizzes,
});

const renderPanel = () =>
  renderWithProviders(<StudentQuizSummary classroomId={CLASSROOM_ID} sessionId={SESSION_ID} />);

describe('StudentQuizSummary', () => {
  afterEach(() => vi.clearAllMocks());

  it('shows what the student picked and which option was actually right', async () => {
    mockSummary.mockResolvedValue(summary([closedQuiz()]));
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: /Photosynthesis/ }));

    const wrong = screen.getByText('Nitrogen').closest('div')!;
    expect(within(wrong).getByText(/Your answer/i)).toBeInTheDocument();

    // The point of a review: the correct option is named even though they did not pick it.
    expect(screen.getByText('Carbon dioxide')).toBeInTheDocument();
  });

  it('offers no review while the quiz is still open', async () => {
    // The review names the correct option, so the server withholds it entirely until the quiz is
    // finished. There must be nothing to expand.
    mockSummary.mockResolvedValue(
      summary([
        closedQuiz({ status: 'Open', questions: [], answeredCount: 1, questionCount: 1 }),
      ]),
    );
    renderPanel();

    expect(await screen.findByText(/In progress/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Photosynthesis/ })).toBeDisabled();
  });

  it('lists a question the student skipped rather than hiding it', async () => {
    mockSummary.mockResolvedValue(
      summary([
        closedQuiz({
          answeredCount: 0,
          questions: [
            {
              ...closedQuiz().questions[0],
              selectedOptionId: null,
              isCorrect: null,
              pointsAwarded: 0,
            },
          ],
        }),
      ]),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: /Photosynthesis/ }));

    expect(screen.getByText(/did not answer this one/i)).toBeInTheDocument();
    expect(screen.getByText('Carbon dioxide')).toBeInTheDocument();
    expect(screen.queryByText(/Your answer/i)).not.toBeInTheDocument();
  });
});
