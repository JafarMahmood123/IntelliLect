import { describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QuizTimeControls } from './QuizTimeControls';
import type { QuizRespondent, QuizTeacher } from '../types';

const respondent = (overrides: Partial<QuizRespondent> = {}): QuizRespondent => ({
  studentId: 'student-1',
  studentName: 'Amina',
  answeredCount: 1,
  hasSubmitted: false,
  closesAtUtc: '2026-01-01T10:05:00Z',
  hasExtraTime: false,
  ...overrides,
});

const quiz = (respondents: QuizRespondent[] = []): QuizTeacher => ({
  id: 'quiz-1',
  sessionId: 'session-1',
  title: 'Caching',
  status: 'Open',
  totalPoints: 2,
  totalSeconds: 120,
  closesAtUtc: '2026-01-01T10:05:00Z',
  serverNowUtc: '2026-01-01T10:00:00Z',
  respondentCount: respondents.length,
  submittedCount: respondents.filter((r) => r.hasSubmitted).length,
  respondents,
  questions: [],
});

describe('QuizTimeControls', () => {
  it('gives the whole class more time in one press', async () => {
    const onExtend = vi.fn();
    render(<QuizTimeControls quiz={quiz()} onExtend={onExtend} busy={false} />);

    await userEvent.click(screen.getByRole('button', { name: '+2 min' }));

    // No student ids at all: everyone.
    expect(onExtend).toHaveBeenCalledWith(120);
  });

  it('gives time to only the students picked', async () => {
    // The case a class-wide extension gets wrong: it would hand the same minutes to everyone who
    // was fine, including anyone who has already finished.
    const onExtend = vi.fn();
    render(
      <QuizTimeControls
        quiz={quiz([
          respondent(),
          respondent({ studentId: 'student-2', studentName: 'Bilal' }),
        ])}
        onExtend={onExtend}
        busy={false}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /certain students/ }));
    await userEvent.click(screen.getByRole('button', { name: /Bilal/ }));
    const picked = screen.getAllByRole('button', { name: '+1 min' });
    await userEvent.click(picked[picked.length - 1]);

    expect(onExtend).toHaveBeenCalledWith(60, ['student-2']);
  });

  it('does not offer more time to a student who has finished', async () => {
    // They locked their answers in on purpose; offering time invites reopening them.
    render(
      <QuizTimeControls
        quiz={quiz([
          respondent(),
          respondent({ studentId: 'student-2', studentName: 'Bilal', hasSubmitted: true }),
        ])}
        onExtend={vi.fn()}
        busy={false}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /certain students/ }));

    expect(screen.getByRole('button', { name: /Amina/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Bilal/ })).not.toBeInTheDocument();
  });

  it('marks a student who already has extra time', async () => {
    render(
      <QuizTimeControls
        quiz={quiz([respondent({ hasExtraTime: true })])}
        onExtend={vi.fn()}
        busy={false}
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /certain students/ }));

    expect(within(screen.getByRole('button', { name: /Amina/ })).getByText('extra')).toBeInTheDocument();
  });

  it('survives a server that does not send the respondent list', () => {
    // An older classroom-service would otherwise take the whole live panel down with it.
    const stale = { ...quiz(), respondents: undefined } as unknown as QuizTeacher;

    render(<QuizTimeControls quiz={stale} onExtend={vi.fn()} busy={false} />);

    expect(screen.getByRole('button', { name: '+1 min' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /certain students/ })).not.toBeInTheDocument();
  });
});
