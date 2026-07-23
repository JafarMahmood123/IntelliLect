import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { ClassroomSessionList } from './ClassroomSessionList';
import { describeSessionEnd, describeSessionEndError } from '../utils/sessionEnd';
import { renderWithProviders } from '../../../test/test-utils';
import type { Session, SessionEndOutcome } from '../types';

vi.mock('../api/classrooms', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/classrooms')>()),
  getClassroomSessions: vi.fn(),
  endSession: vi.fn(),
}));

import { getClassroomSessions, endSession } from '../api/classrooms';

const mockGetSessions = vi.mocked(getClassroomSessions);
const mockEndSession = vi.mocked(endSession);

const CLASSROOM_ID = 'class-1';
const SESSION_ID = 'session-1';

const session = (status: Session['status']): Session => ({
  id: SESSION_ID,
  classroomId: CLASSROOM_ID,
  title: 'Lecture 4',
  description: 'Kinematics',
  status,
  scheduledAtUtc: '2026-06-01T10:00:00Z',
  participationMode: 0,
});

const okOutcome: SessionEndOutcome = {
  sessionId: SESSION_ID,
  status: 'Ended',
  alreadyEnded: false,
  streamEnded: true,
  summaryTriggered: true,
  endedAtUtc: '2026-06-01T11:00:00Z',
};

const renderList = (isTeacher: boolean) =>
  renderWithProviders(
    <MemoryRouter>
      <ClassroomSessionList classroomId={CLASSROOM_ID} isTeacher={isTeacher} />
    </MemoryRouter>,
  );

describe('describeSessionEnd', () => {
  it('reports a clean end as a success', () => {
    expect(describeSessionEnd(okOutcome).type).toBe('success');
  });

  it('warns — rather than claiming success — when a teardown step failed', () => {
    const noSummary = describeSessionEnd({ ...okOutcome, summaryTriggered: false });
    expect(noSummary.type).toBe('warning');
    expect(noSummary.message).toMatch(/summary could not be started/i);

    const noStream = describeSessionEnd({ ...okOutcome, streamEnded: false });
    expect(noStream.type).toBe('warning');
    expect(noStream.message).toMatch(/leave manually/i);
  });

  it('treats an already-ended session as information, not an error', () => {
    expect(describeSessionEnd({ ...okOutcome, alreadyEnded: true }).type).toBe('info');
  });
});

describe('describeSessionEndError', () => {
  it('explains the two refusals a teacher can actually hit', () => {
    expect(describeSessionEndError({ response: { status: 403 } })).toMatch(/owns this classroom/i);
    expect(describeSessionEndError({ response: { status: 404 } })).toMatch(/no longer exists/i);
  });

  it("prefers the API's own explanation, and falls back when there is none", () => {
    expect(
      describeSessionEndError({ response: { status: 409, data: { detail: 'Session is closing.' } } }),
    ).toBe('Session is closing.');
    expect(describeSessionEndError(new Error('network down'))).toMatch(/could not end the session/i);
  });
});

describe('ClassroomSessionList — ending a session', () => {
  afterEach(() => vi.clearAllMocks());

  it('offers End Session to the teacher only while the session is live', async () => {
    mockGetSessions.mockResolvedValue([session('Live')]);
    renderList(true);

    expect(await screen.findByRole('button', { name: /end session/i })).toBeInTheDocument();
  });

  it('does not offer it for a session that is not live', async () => {
    mockGetSessions.mockResolvedValue([session('Scheduled')]);
    renderList(true);

    await screen.findByText('Lecture 4');
    expect(screen.queryByRole('button', { name: /end session/i })).not.toBeInTheDocument();
  });

  it('never offers it to a student, even on a live session', async () => {
    mockGetSessions.mockResolvedValue([session('Live')]);
    renderList(false);

    await screen.findByText('Lecture 4');
    expect(screen.queryByRole('button', { name: /end session/i })).not.toBeInTheDocument();
  });

  it('confirms before ending — the click alone must not close the session', async () => {
    mockGetSessions.mockResolvedValue([session('Live')]);
    mockEndSession.mockResolvedValue(okOutcome);
    renderList(true);

    await userEvent.click(await screen.findByRole('button', { name: /end session/i }));

    expect(await screen.findByText(/end this session\?/i)).toBeInTheDocument();
    expect(mockEndSession).not.toHaveBeenCalled();
  });

  it('ends the session once the teacher confirms', async () => {
    mockGetSessions.mockResolvedValue([session('Live')]);
    mockEndSession.mockResolvedValue(okOutcome);
    renderList(true);

    await userEvent.click(await screen.findByRole('button', { name: /end session/i }));
    await screen.findByText(/end this session\?/i);
    // The dialog's own confirm button, not the row button that opened it.
    const [, confirmButton] = screen.getAllByRole('button', { name: /end session/i });
    await userEvent.click(confirmButton);

    await waitFor(() => expect(mockEndSession).toHaveBeenCalledWith(CLASSROOM_ID, SESSION_ID));
  });

  it('surfaces a failure instead of pretending the session closed', async () => {
    mockGetSessions.mockResolvedValue([session('Live')]);
    mockEndSession.mockRejectedValue({ response: { status: 403 } });
    renderList(true);

    await userEvent.click(await screen.findByRole('button', { name: /end session/i }));
    await screen.findByText(/end this session\?/i);
    const [, confirmButton] = screen.getAllByRole('button', { name: /end session/i });
    await userEvent.click(confirmButton);

    expect(await screen.findByText(/owns this classroom/i)).toBeInTheDocument();
  });
});
