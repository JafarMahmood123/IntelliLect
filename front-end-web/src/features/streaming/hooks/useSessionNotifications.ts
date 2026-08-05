import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChatMessage } from './useStreamHub';

/**
 * Whether desktop notifications can be used, and whether they have been allowed.
 *
 * `unsupported` is a real state, not a stand-in for `denied`: a browser without the API is not a
 * user who said no, and the difference decides whether there is a button worth showing.
 */
export type DesktopPermission = 'unsupported' | 'default' | 'granted' | 'denied';

interface Options {
  /** Append-only chat log from the session hub. */
  messages: ChatMessage[];
  /** Used to tell your own messages from everyone else's. */
  currentUserId: string | undefined;
  /** The chat panel is the open section of the drawer. */
  isChatOpen: boolean;
  /** The quiz currently accepting answers, or null. */
  openQuiz: { id: string; title: string } | null;
  /** The quiz panel is the open section of the drawer. */
  isQuizOpen: boolean;
}

const readPermission = (): DesktopPermission => {
  // jsdom has no Notification, and neither does an insecure origin. Reading it defensively is the
  // difference between a degraded feature and a crashed session page.
  if (typeof window === 'undefined' || !('Notification' in window)) return 'unsupported';
  return Notification.permission as DesktopPermission;
};

/** Raises a desktop notification if allowed, and never throws if it is not. */
const notifyDesktop = (title: string, body: string) => {
  if (readPermission() !== 'granted') return;
  try {
    const notification = new Notification(title, { body, tag: 'intellilect-session' });
    // The whole point is that they are elsewhere — clicking has to bring them back.
    notification.onclick = () => {
      window.focus();
      notification.close();
    };
  } catch {
    // Some browsers throw for notifications outside a service worker. In-app already covered it.
  }
};

/**
 * Tells someone that something happened in the live session while they were not looking at it.
 *
 * Three carriers, deliberately in this order of ambition:
 *   1. an in-app unread count, for the person who is on the session page with the drawer closed;
 *   2. the document title, which is the only one that reaches a BACKGROUNDED tab without asking
 *      anyone's permission — a tab strip reading "(3) IntelliLect" is the cheapest possible
 *      answer to "I don't want to sit on this tab";
 *   3. a desktop notification, for the person who has switched to another application entirely.
 *
 * Only the third needs permission, and it is never requested on its own initiative — see
 * `requestDesktop`.
 *
 * SCOPE: this lives with the session drawer, so it works while the session page is mounted —
 * including when its tab is backgrounded or the whole window is behind something else, which is
 * what the feature was asked for. It does NOT survive navigating to another route in the app: the
 * hub connection is torn down with the page, and keeping it alive would mean hoisting a
 * session-scoped socket above the router. That is a larger change and a separate decision.
 */
export const useSessionNotifications = ({
  messages,
  currentUserId,
  isChatOpen,
  openQuiz,
  isQuizOpen,
}: Options) => {
  const [unreadChat, setUnreadChat] = useState(0);
  const [muted, setMuted] = useState(false);
  const [permission, setPermission] = useState<DesktopPermission>(readPermission);

  // How much of the log has already been judged. A COUNT rather than derived state, because the
  // decision "was this unread?" depends on what the drawer looked like at the moment the message
  // arrived — replaying it on a later render would resurrect a count that was already cleared.
  //
  // It also makes the effect below safe to re-run for any reason: the second pass finds nothing
  // fresh and returns, which is why `isChatOpen` and `muted` can be honest dependencies instead of
  // refs smuggled past the dependency array.
  const seenCount = useRef(messages.length);

  useEffect(() => {
    // A shrinking log needs no special case: the slice comes back empty, the counter resets to the
    // new length on the next line, and nothing is announced. The log is append-only for the life of
    // a connection anyway — a shorter one means a different session's, and that arrives with a
    // remounted drawer.
    const fresh = messages.slice(seenCount.current);
    seenCount.current = messages.length;
    if (fresh.length === 0) return;

    // Your own message is not news to you — it is the thing you just did.
    const fromOthers = fresh.filter((message) => message.userId !== currentUserId);
    if (fromOthers.length === 0) return;

    // Watching the panel it arrived in means it has already been seen. Visibility matters as much
    // as the open panel: a chat panel open in a tab nobody is looking at has shown nobody anything.
    const watching = isChatOpen && document.visibilityState === 'visible';
    if (watching) return;

    setUnreadChat((count) => count + fromOthers.length);

    if (muted) return;

    const latest = fromOthers[fromOthers.length - 1];
    notifyDesktop(
      fromOthers.length === 1 ? latest.userName : `${fromOthers.length} new messages`,
      latest.message,
    );
  }, [messages, currentUserId, isChatOpen, muted]);

  // Opening the panel is what marks chat read, called from the drawer's open handler rather than
  // from an effect on `isChatOpen` — an effect would re-clear on any later render, including one
  // caused by a message that legitimately arrived while the tab was in the background.
  const markChatRead = useCallback(() => setUnreadChat(0), []);

  // Coming back to a backgrounded tab that already had the chat panel open is the other way of
  // seeing a message. Without this the badge would sit there unread over messages the user is
  // currently looking at, and only a pointless close-and-reopen would clear it.
  useEffect(() => {
    if (!isChatOpen) return;
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') setUnreadChat(0);
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, [isChatOpen]);

  // A quiz is announced once, when it appears. Re-announcing on every refetch of the same quiz
  // would turn one event into a stream of them.
  const announcedQuizId = useRef<string | null>(null);
  useEffect(() => {
    if (!openQuiz || announcedQuizId.current === openQuiz.id) return;
    announcedQuizId.current = openQuiz.id;

    if (muted) return;
    if (isQuizOpen && document.visibilityState === 'visible') return;

    notifyDesktop('Quiz started', `${openQuiz.title || 'A quiz'} is open for answers.`);
  }, [openQuiz, isQuizOpen, muted]);

  const quizWaiting = Boolean(openQuiz) && !isQuizOpen;

  // The title is the only carrier that reaches a backgrounded tab for free, so it earns its
  // side effect. The original is captured once and restored on the way out, so leaving the
  // session never leaves a stale "(3)" on an unrelated page.
  useEffect(() => {
    const original = document.title;
    return () => {
      document.title = original;
    };
  }, []);

  useEffect(() => {
    const base = document.title.replace(/^\(\d+\)\s*/, '');
    const pending = unreadChat + (quizWaiting ? 1 : 0);
    document.title = pending > 0 && !muted ? `(${pending}) ${base}` : base;
  }, [unreadChat, quizWaiting, muted]);

  /**
   * Asks the browser for permission. MUST be called from a user gesture — that is the whole
   * reason this is a returned function rather than something the hook does on mount. An
   * unprompted permission dialog on entering a class is exactly the behaviour that gets a site
   * permanently blocked, and a blocked site cannot notify anyone about anything.
   *
   * A previous refusal is respected permanently: the browser would not re-prompt anyway, and
   * asking again is how a feature becomes a nuisance.
   */
  const requestDesktop = useCallback(async () => {
    if (readPermission() !== 'default') return;
    try {
      setPermission((await Notification.requestPermission()) as DesktopPermission);
    } catch {
      setPermission(readPermission());
    }
  }, []);

  return {
    unreadChat,
    quizWaiting,
    markChatRead,
    muted,
    toggleMuted: useCallback(() => setMuted((value) => !value), []),
    permission,
    requestDesktop,
  };
};
