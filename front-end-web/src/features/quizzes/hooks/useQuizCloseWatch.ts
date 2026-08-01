import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { quizKeys } from './useQuizQueries';

/** How often to re-read after the timer expires, until the server confirms the close. */
const POLL_MS = 3000;

/**
 * Re-reads the quiz once its timer has run out, until the server agrees it is over.
 *
 * The server closes timed-out quizzes on a short sweep and broadcasts it, so in the ordinary case
 * the panel updates without this. It exists for the case where that one message does not arrive —
 * a dropped socket, a reconnect mid-sweep — which would otherwise strand the panel showing a
 * finished quiz with a dead "Time up" clock and no marks, forever.
 *
 * Polling ONLY while expired, and only until the read comes back changed. A quiz still running is
 * driven by the broadcast alone, exactly as before.
 */
export const useQuizCloseWatch = (
  sessionId: string,
  quizId: string | undefined,
  expired: boolean,
) => {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!expired || !quizId) return;

    const reread = () => {
      queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.detail(quizId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.studentView(quizId) });
      // The marks the close releases. Without this the quiz disappears from the panel while the
      // summary underneath it still shows the pre-close zero.
      queryClient.invalidateQueries({ queryKey: quizKeys.mySessionSummary(sessionId) });
      queryClient.invalidateQueries({ queryKey: quizKeys.sessionSummary(sessionId) });
    };

    reread();
    const id = window.setInterval(reread, POLL_MS);
    return () => window.clearInterval(id);
  }, [expired, quizId, sessionId, queryClient]);
};
