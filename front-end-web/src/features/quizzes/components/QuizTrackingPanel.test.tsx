import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { QuizTrackingPanel } from './QuizTrackingPanel';
import { renderWithProviders } from '../../../test/test-utils';
import type { ClassroomQuizTracking, MyClassroomQuizTracking } from '../types';

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
  extendQuiz: vi.fn(),
  getQuizForTeacher: vi.fn(),
  getQuizResults: vi.fn(),
  getOpenQuizForSession: vi.fn(),
  getQuizForStudent: vi.fn(),
  submitQuizAnswer: vi.fn(),
  submitQuiz: vi.fn(),
  getMyQuizResult: vi.fn(),
  getSessionQuizSummary: vi.fn(),
  getMySessionQuizSummary: vi.fn(),
  getClassroomQuizTracking: vi.fn(),
  getMyClassroomQuizTracking: vi.fn(),
}));

import { getClassroomQuizTracking, getMyClassroomQuizTracking } from '../api/quizzes';

const mockTeacher = vi.mocked(getClassroomQuizTracking);
const mockStudent = vi.mocked(getMyClassroomQuizTracking);

const CLASSROOM_ID = 'class-1';

const teacherTracking = (
  overrides: Partial<ClassroomQuizTracking> = {},
): ClassroomQuizTracking => ({
  classroomId: CLASSROOM_ID,
  enrolledStudentCount: 3,
  activeStudentCount: 2,
  sessionCount: 4,
  sessionsWithQuizzesCount: 2,
  quizCount: 3,
  totalPointsAvailable: 15,
  classAveragePercentage: 60,
  students: [
    {
      studentId: 'student-1',
      studentName: 'Amina',
      quizzesTaken: 3,
      quizCount: 3,
      answeredCount: 3,
      correctCount: 3,
      score: 15,
      totalPointsAvailable: 15,
      percentage: 100,
      sessionsTakenPart: 2,
      sessionsWithQuizzesCount: 2,
    },
    {
      studentId: 'student-2',
      studentName: 'Bilal',
      quizzesTaken: 1,
      quizCount: 3,
      answeredCount: 1,
      correctCount: 0,
      score: 0,
      totalPointsAvailable: 15,
      percentage: 0,
      sessionsTakenPart: 1,
      sessionsWithQuizzesCount: 2,
    },
  ],
  sessions: [
    {
      sessionId: 'session-2',
      title: 'Eviction',
      scheduledAtUtc: '2026-01-08T09:00:00Z',
      startedAtUtc: null,
      quizCount: 1,
      totalPoints: 5,
      participantCount: 1,
      averagePercentage: 20,
    },
  ],
  ...overrides,
});

const studentTracking = (
  overrides: Partial<MyClassroomQuizTracking> = {},
): MyClassroomQuizTracking => ({
  classroomId: CLASSROOM_ID,
  score: 10,
  totalPointsAvailable: 15,
  percentage: 67,
  quizzesTaken: 2,
  quizCount: 3,
  sessionsTakenPart: 1,
  sessionsWithQuizzesCount: 2,
  classAveragePercentage: 60,
  sessions: [
    {
      sessionId: 'session-1',
      title: 'Caching',
      scheduledAtUtc: '2026-01-01T09:00:00Z',
      startedAtUtc: null,
      score: 10,
      totalPoints: 10,
      percentage: 100,
      quizzesTaken: 2,
      quizCount: 2,
    },
    {
      sessionId: 'session-2',
      title: 'Eviction',
      scheduledAtUtc: '2026-01-08T09:00:00Z',
      startedAtUtc: null,
      score: 0,
      totalPoints: 5,
      percentage: 0,
      quizzesTaken: 0,
      quizCount: 1,
    },
  ],
  ...overrides,
});

describe('QuizTrackingPanel', () => {
  afterEach(() => vi.clearAllMocks());

  it('shows the teacher the class size, quiz count and every cumulative score', async () => {
    mockTeacher.mockResolvedValue(teacherTracking());
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher />);

    expect(await screen.findByText('Class average')).toBeInTheDocument();
    expect(screen.getByText('3 enrolled')).toBeInTheDocument();
    expect(screen.getByText('15 marks')).toBeInTheDocument();

    // Each student's standing is against what the CLASS was offered, so the denominator is shared.
    expect(screen.getByText('Amina')).toBeInTheDocument();
    expect(screen.getByText(/1 of 3 quizzes/)).toBeInTheDocument();
  });

  it('tells a student where they stand without naming anyone', async () => {
    mockStudent.mockResolvedValue(studentTracking());
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher={false} />);

    expect(await screen.findByText('10/15')).toBeInTheDocument();
    expect(screen.getByText('you are 7% above')).toBeInTheDocument();
  });

  it('lists a session the student missed rather than hiding it', async () => {
    // Hiding it would leave them wondering why their percentage is lower than their rows say.
    mockStudent.mockResolvedValue(studentTracking());
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher={false} />);

    const missed = (await screen.findByText('Eviction')).closest('div')!;
    expect(within(missed).getByText(/did not take this one/)).toBeInTheDocument();
  });

  it('says so plainly when no quiz has run yet', async () => {
    mockTeacher.mockResolvedValue(teacherTracking({ quizCount: 0, students: [], sessions: [] }));
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher />);

    expect(await screen.findByText('No quizzes yet')).toBeInTheDocument();
  });
});
