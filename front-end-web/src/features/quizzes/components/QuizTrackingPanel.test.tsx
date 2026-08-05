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
      rank: 1,
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
      rank: 2,
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
  rank: 2,
  rankedStudentCount: 3,
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

  it('shows the position the server ranked each student at, not the row number', async () => {
    // Two students tied at the top both hold position 1, and the next one is 3rd. Numbering the
    // rows would show 1, 2, 3 and invent a winner the marks do not support.
    mockTeacher.mockResolvedValue(
      teacherTracking({
        students: [
          { ...teacherTracking().students[0], studentId: 'a', studentName: 'Amina', rank: 1 },
          { ...teacherTracking().students[0], studentId: 'b', studentName: 'Bilal', rank: 1 },
          { ...teacherTracking().students[1], studentId: 'c', studentName: 'Carim', rank: 3 },
        ],
      }),
    );
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher />);

    await screen.findByText('Amina');
    // Both of the tied pair hold position 1; nobody holds 2, because the tie consumed it.
    expect(screen.getAllByLabelText('Rank 1')).toHaveLength(2);
    expect(screen.queryByLabelText('Rank 2')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Rank 3')).toBeInTheDocument();
  });

  it('tells a student their own position and how many it is out of', async () => {
    mockStudent.mockResolvedValue(studentTracking({ rank: 2, rankedStudentCount: 5 }));
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher={false} />);

    expect(await screen.findByText('#2')).toBeInTheDocument();
    expect(screen.getByText('of 5')).toBeInTheDocument();
  });

  it('names no classmate in the student view', async () => {
    // The privacy rule this ranking is built around: a position, a headcount, and nothing else.
    mockStudent.mockResolvedValue(studentTracking());
    const { container } = renderWithProviders(
      <QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher={false} />,
    );

    await screen.findByText('#2');
    expect(container.textContent).not.toContain('Amina');
    expect(container.textContent).not.toContain('Bilal');
  });

  it('shows a dash rather than a last place for a student who has taken nothing', async () => {
    mockStudent.mockResolvedValue(studentTracking({ rank: null, rankedStudentCount: 4 }));
    renderWithProviders(<QuizTrackingPanel classroomId={CLASSROOM_ID} isTeacher={false} />);

    expect(await screen.findByText('—')).toBeInTheDocument();
    expect(screen.getByText('no graded quiz yet')).toBeInTheDocument();
  });
});
