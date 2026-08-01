import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TeacherQuizPanel } from './TeacherQuizPanel';
import { renderWithProviders } from '../../../test/test-utils';
import type { QuizTeacher } from '../types';

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

import {
  createQuizDraft,
  generateQuizDraft,
  getOpenQuizForSession,
  getQuizForTeacher,
  getQuizLimits,
  getSessionQuizSummary,
  publishQuiz,
  updateQuizDraft,
} from '../api/quizzes';

const mockLimits = vi.mocked(getQuizLimits);
const mockOpenQuiz = vi.mocked(getOpenQuizForSession);
const mockTeacherQuiz = vi.mocked(getQuizForTeacher);
const mockCreate = vi.mocked(createQuizDraft);
const mockUpdate = vi.mocked(updateQuizDraft);
const mockPublish = vi.mocked(publishQuiz);
const mockSummary = vi.mocked(getSessionQuizSummary);
const mockGenerate = vi.mocked(generateQuizDraft);

const CLASSROOM_ID = 'class-1';
const SESSION_ID = 'session-1';

const draft = (overrides: Partial<QuizTeacher> = {}): QuizTeacher => ({
  id: 'quiz-1',
  sessionId: SESSION_ID,
  title: '',
  status: 'Draft',
  totalPoints: 1,
  totalSeconds: 60,
  closesAtUtc: null,
  serverNowUtc: '2026-01-01T10:00:00Z',
  respondentCount: 0,
  submittedCount: 0,
  questions: [],
  ...overrides,
});

/** A 409 as the api client surfaces it — the shape the panel reads to recognise the cause. */
const conflict = () => Object.assign(new Error('conflict'), { response: { status: 409 } });

const setup = () => {
  mockLimits.mockResolvedValue({
    maxQuestionsPerQuiz: 5,
    minAnswersPerQuestion: 2,
    maxAnswersPerQuestion: 4,
    defaultSecondsPerQuestion: 60,
    maxQuizDurationSeconds: 600,
  });
  mockOpenQuiz.mockResolvedValue(null);
  mockTeacherQuiz.mockResolvedValue(draft());
  mockSummary.mockResolvedValue({
    sessionId: SESSION_ID,
    quizCount: 0,
    countedQuizCount: 0,
    totalPointsAvailable: 0,
    students: [],
    questions: [],
  });

  return renderWithProviders(
    <TeacherQuizPanel classroomId={CLASSROOM_ID} sessionId={SESSION_ID} liveEvent={null} />,
  );
};

/** Adds one question with text, which is what makes the save and publish buttons appear. */
const composeOneQuestion = async () => {
  await userEvent.click(await screen.findByRole('button', { name: /Add question/ }));
  await userEvent.type(screen.getByPlaceholderText('Question 1'), 'What is a cache?');
};

describe('TeacherQuizPanel', () => {
  afterEach(() => vi.clearAllMocks());

  it('clears the composer once the quiz has gone out to the class', async () => {
    // Publishing hands the quiz to the room. Leaving it in the composer meant the next press tried
    // to rewrite a published quiz, which the server refuses — reported as "could not save".
    mockCreate.mockResolvedValue(draft());
    mockPublish.mockResolvedValue(draft({ status: 'Open' }));
    setup();

    await composeOneQuestion();
    await userEvent.click(screen.getByRole('button', { name: /Publish to students/ }));

    await waitFor(() => expect(mockPublish).toHaveBeenCalledWith(CLASSROOM_ID, 'quiz-1'));
    await waitFor(() =>
      expect(screen.queryByPlaceholderText('Question 1')).not.toBeInTheDocument(),
    );
  });

  it('saves a draft without publishing it', async () => {
    mockCreate.mockResolvedValue(draft());
    setup();

    await composeOneQuestion();
    await userEvent.click(screen.getByRole('button', { name: /Save draft/ }));

    await waitFor(() => expect(mockCreate).toHaveBeenCalled());
    expect(mockPublish).not.toHaveBeenCalled();
    // Still editable: saving is not publishing.
    expect(screen.getByPlaceholderText('Question 1')).toBeInTheDocument();
  });

  it('warns the teacher when the answer key follows the material instead of what they said', async () => {
    // Correcting a slip silently would be worse than the slip: the teacher would publish an answer
    // key contradicting what they told the room and never learn why students protested.
    mockGenerate.mockResolvedValue({
      quiz: draft({
        title: 'Caching',
        questions: [
          {
            id: 'q-1',
            order: 0,
            text: 'What is the target hit rate?',
            points: 1,
            timeLimitSeconds: 60,
            options: [
              { id: 'o-1', order: 0, text: '85%', isCorrect: true, selectedCount: 0 },
              { id: 'o-2', order: 1, text: '55%', isCorrect: false, selectedCount: 0 },
            ],
          },
        ],
      }),
      corrections: [{ taught: 'the hit rate should be 55%', corrected: 'the target is 85%' }],
    });
    setup();

    await userEvent.click(await screen.findByRole('button', { name: /^Quick test$/ }));

    expect(await screen.findByText(/Answered from your course material/)).toBeInTheDocument();
    expect(screen.getByText(/the hit rate should be 55%/)).toBeInTheDocument();
    expect(screen.getByText(/the target is 85%/)).toBeInTheDocument();
  });

  it('asks for the whole lesson only when the full-quiz button is pressed', async () => {
    // The two buttons differ ONLY in this flag — quick test reads the recent ideas, full quiz
    // reads the session transcript — so it is the whole of what distinguishes them.
    mockGenerate.mockResolvedValue({ quiz: draft(), corrections: [] });
    setup();

    await userEvent.click(await screen.findByRole('button', { name: /^Quick test$/ }));
    await waitFor(() =>
      expect(mockGenerate).toHaveBeenLastCalledWith(CLASSROOM_ID, SESSION_ID, 3, false),
    );

    await userEvent.click(screen.getByRole('button', { name: /^Full quiz$/ }));
    await waitFor(() =>
      expect(mockGenerate).toHaveBeenLastCalledWith(CLASSROOM_ID, SESSION_ID, 10, true),
    );
  });

  it('says nothing when the assistant agreed with the teacher', async () => {
    mockGenerate.mockResolvedValue({ quiz: draft(), corrections: [] });
    setup();

    await userEvent.click(await screen.findByRole('button', { name: /^Quick test$/ }));

    await waitFor(() => expect(mockGenerate).toHaveBeenCalled());
    expect(screen.queryByText(/Answered from your course material/)).not.toBeInTheDocument();
  });

  it('passes on the assistant’s reason when there is nothing new to quiz', async () => {
    // "Keep talking" and "talk about something NEW" are different instructions, and the second is
    // what a teacher gets after generating twice from one explanation.
    mockGenerate.mockRejectedValue(
      Object.assign(new Error('conflict'), {
        response: {
          status: 409,
          data: { detail: 'Everything said since the last quiz has already been used.' },
        },
      }),
    );
    setup();

    await userEvent.click(await screen.findByRole('button', { name: /^Quick test$/ }));

    expect(await screen.findByText(/already been used/)).toBeInTheDocument();
  });

  it('clears itself instead of retrying when the quiz has already been published', async () => {
    // The state the teacher actually hit: a composer still holding a quiz that had been published
    // and closed. "Please try again" would have failed identically every time.
    mockCreate.mockResolvedValue(draft());
    mockUpdate.mockRejectedValue(conflict());
    setup();

    await composeOneQuestion();
    await userEvent.click(screen.getByRole('button', { name: /Save draft/ }));
    await waitFor(() => expect(mockCreate).toHaveBeenCalled());

    await userEvent.click(screen.getByRole('button', { name: /Publish to students/ }));

    expect(await screen.findByText(/Already published/)).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.queryByPlaceholderText('Question 1')).not.toBeInTheDocument(),
    );
    expect(mockPublish).not.toHaveBeenCalled();
  });
});
