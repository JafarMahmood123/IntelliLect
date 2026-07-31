import { useEffect, useState } from 'react';

/**
 * Seconds remaining on a quiz, counted against the SERVER's clock.
 *
 * The offset matters: a device whose clock is minutes off would otherwise show a wrong countdown —
 * or a quiz that looks already-expired — while the server happily accepts answers. Both timestamps
 * come from the same payload, so the difference between them is the skew, measured once.
 *
 * Returns null when the quiz has no deadline (a draft), and 0 once it has run out. The countdown is
 * DISPLAY ONLY — the server rejects late answers on its own, so nothing here can be trusted or
 * tampered into extra time.
 */
export const useQuizCountdown = (
  closesAtUtc: string | null | undefined,
  serverNowUtc: string | null | undefined,
): number | null => {
  const [remaining, setRemaining] = useState<number | null>(null);

  useEffect(() => {
    if (!closesAtUtc || !serverNowUtc) {
      setRemaining(null);
      return;
    }

    const closesAt = new Date(closesAtUtc).getTime();
    const serverNow = new Date(serverNowUtc).getTime();
    if (Number.isNaN(closesAt) || Number.isNaN(serverNow)) {
      setRemaining(null);
      return;
    }

    // How far this device's clock is from the server's, fixed at the moment the payload arrived.
    const skew = Date.now() - serverNow;

    const tick = () => {
      const serverTimeNow = Date.now() - skew;
      setRemaining(Math.max(0, Math.round((closesAt - serverTimeNow) / 1000)));
    };

    tick();
    const id = window.setInterval(tick, 1000);
    return () => window.clearInterval(id);
  }, [closesAtUtc, serverNowUtc]);

  return remaining;
};

/** mm:ss for display. */
export const formatCountdown = (seconds: number | null): string => {
  if (seconds === null) return '--:--';
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${m}:${s.toString().padStart(2, '0')}`;
};
