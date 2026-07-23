import type { SessionEndOutcome } from '../types';

export interface SessionEndToast {
  type: 'success' | 'warning' | 'info' | 'error';
  title: string;
  message: string;
}

/**
 * Turns the outcome of ending a session into the message shown to the teacher. Shared by the
 * session list and the live room so both report the same thing.
 *
 * Ending is best-effort past the point where the session is marked over, so there are three
 * outcomes worth distinguishing: it ended cleanly, it ended but a teardown step failed (the
 * teacher should know the summary may not appear), or it was already over.
 */
export const describeSessionEnd = (outcome: SessionEndOutcome): SessionEndToast => {
  if (outcome.alreadyEnded) {
    return {
      type: 'info',
      title: 'Already Ended',
      message: 'This session had already been closed.',
    };
  }

  if (!outcome.streamEnded || !outcome.summaryTriggered) {
    const problems = [
      !outcome.streamEnded && 'some participants may still need to leave manually',
      !outcome.summaryTriggered && 'the summary could not be started automatically',
    ].filter(Boolean);

    return {
      type: 'warning',
      title: 'Session Ended With Warnings',
      message: `The session is closed, but ${problems.join(' and ')}.`,
    };
  }

  return {
    type: 'success',
    title: 'Session Ended',
    message: 'Everyone has been disconnected. The recording and summary are being prepared.',
  };
};

/** Message for a failed end request, preferring the API's own explanation. */
export const describeSessionEndError = (error: unknown): string => {
  const response = (error as { response?: { status?: number; data?: { detail?: string } } })?.response;

  if (response?.status === 403) return 'Only the teacher who owns this classroom can end its sessions.';
  if (response?.status === 404) return 'This session no longer exists.';

  return response?.data?.detail ?? 'Could not end the session. Please try again.';
};
