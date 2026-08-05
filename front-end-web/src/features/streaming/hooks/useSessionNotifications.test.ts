import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { useSessionNotifications } from './useSessionNotifications';
import type { ChatMessage } from './useStreamHub';

const ME = 'me';
const THEM = 'them';

const message = (userId: string, text: string): ChatMessage => ({
  userId,
  userName: userId === ME ? 'Amina' : 'Bilal',
  message: text,
  timestamp: new Date('2026-01-01T10:00:00Z'),
});

/**
 * jsdom has no Notification, which is itself one of the cases under test — so it is installed
 * per-test rather than globally, and removed again afterwards.
 */
const installNotification = (permission: NotificationPermission) => {
  const constructed: Array<{ title: string; body?: string }> = [];
  const requestPermission = vi.fn(async () => permission);

  class FakeNotification {
    onclick: (() => void) | null = null;
    close = vi.fn();
    constructor(title: string, options?: NotificationOptions) {
      constructed.push({ title, body: options?.body });
    }
    static permission = permission;
    static requestPermission = requestPermission;
  }

  vi.stubGlobal('Notification', FakeNotification);
  return { constructed, requestPermission };
};

const setVisibility = (state: DocumentVisibilityState) =>
  Object.defineProperty(document, 'visibilityState', {
    configurable: true,
    get: () => state,
  });

const options = (over: Partial<Parameters<typeof useSessionNotifications>[0]> = {}) => ({
  messages: [] as ChatMessage[],
  currentUserId: ME,
  isChatOpen: false,
  openQuiz: null,
  isQuizOpen: false,
  ...over,
});

describe('useSessionNotifications', () => {
  beforeEach(() => {
    setVisibility('visible');
    document.title = 'IntelliLect';
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('counts a message that arrives while the chat panel is closed', () => {
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ messages: [message(THEM, 'hello')] }));

    expect(result.current.unreadChat).toBe(1);
  });

  it('never counts your own message', () => {
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ messages: [message(ME, 'hello')] }));

    expect(result.current.unreadChat).toBe(0);
  });

  it('does not count a message arriving in the open, visible chat panel', () => {
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options({ isChatOpen: true }),
    });

    rerender(options({ isChatOpen: true, messages: [message(THEM, 'hello')] }));

    expect(result.current.unreadChat).toBe(0);
  });

  it('does count it when the chat panel is open in a backgrounded tab', () => {
    // An open panel in a tab nobody is looking at has shown nobody anything.
    setVisibility('hidden');
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options({ isChatOpen: true }),
    });

    rerender(options({ isChatOpen: true, messages: [message(THEM, 'hello')] }));

    expect(result.current.unreadChat).toBe(1);
  });

  it('clears on open and does not resurrect on a later render', () => {
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });
    rerender(options({ messages: [message(THEM, 'one'), message(THEM, 'two')] }));
    expect(result.current.unreadChat).toBe(2);

    act(() => result.current.markChatRead());
    expect(result.current.unreadChat).toBe(0);

    // Same message list, another render — the count must stay cleared.
    rerender(options({ isChatOpen: true, messages: [message(THEM, 'one'), message(THEM, 'two')] }));
    expect(result.current.unreadChat).toBe(0);
  });

  it('clears when the user returns to a tab that already had chat open', () => {
    setVisibility('hidden');
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options({ isChatOpen: true }),
    });
    rerender(options({ isChatOpen: true, messages: [message(THEM, 'hello')] }));
    expect(result.current.unreadChat).toBe(1);

    setVisibility('visible');
    act(() => document.dispatchEvent(new Event('visibilitychange')));

    expect(result.current.unreadChat).toBe(0);
  });

  it('puts the pending count in the tab title, and takes it away again', () => {
    const { result, rerender, unmount } = renderHook(
      (props) => useSessionNotifications(props),
      { initialProps: options() },
    );

    rerender(options({ messages: [message(THEM, 'one'), message(THEM, 'two')] }));
    expect(document.title).toBe('(2) IntelliLect');

    act(() => result.current.markChatRead());
    expect(document.title).toBe('IntelliLect');

    rerender(options({ messages: [message(THEM, 'one'), message(THEM, 'two')] }));
    unmount();
    // Leaving the session must not leave a stale count on an unrelated page.
    expect(document.title).toBe('IntelliLect');
  });

  it('counts a waiting quiz in the title alongside unread chat', () => {
    const { rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(
      options({
        messages: [message(THEM, 'one')],
        openQuiz: { id: 'q1', title: 'Optics' },
      }),
    );

    expect(document.title).toBe('(2) IntelliLect');
  });

  it('raises one desktop notification per batch when permission is granted', () => {
    const { constructed } = installNotification('granted');
    const { rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ messages: [message(THEM, 'one'), message(THEM, 'two')] }));

    // One alert for the batch, not one per message — two arriving together is one interruption.
    expect(constructed).toHaveLength(1);
    expect(constructed[0].title).toBe('2 new messages');
    expect(constructed[0].body).toBe('two');
  });

  it('names the sender when a single message arrives', () => {
    const { constructed } = installNotification('granted');
    const { rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ messages: [message(THEM, 'hello')] }));

    expect(constructed[0].title).toBe('Bilal');
  });

  it('announces a quiz once, not on every refetch of the same quiz', () => {
    const { constructed } = installNotification('granted');
    const quiz = { id: 'q1', title: 'Optics' };
    const { rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ openQuiz: quiz }));
    rerender(options({ openQuiz: { ...quiz } })); // same id, new object identity
    rerender(options({ openQuiz: { ...quiz } }));

    expect(constructed.filter((n) => n.title === 'Quiz started')).toHaveLength(1);
  });

  it('degrades to in-app only when permission was refused, and never asks again', async () => {
    const { constructed, requestPermission } = installNotification('denied');
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ messages: [message(THEM, 'hello')] }));

    expect(result.current.permission).toBe('denied');
    expect(constructed).toHaveLength(0);
    // The in-app half still works — that is what "degrades" means.
    expect(result.current.unreadChat).toBe(1);

    await act(async () => {
      await result.current.requestDesktop();
    });
    expect(requestPermission).not.toHaveBeenCalled();
  });

  it('survives a browser with no Notification API at all', () => {
    // jsdom's default. Not the same as a refusal, and it must not throw.
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    rerender(options({ messages: [message(THEM, 'hello')] }));

    expect(result.current.permission).toBe('unsupported');
    expect(result.current.unreadChat).toBe(1);
  });

  it('asks for permission only when told to', async () => {
    const { requestPermission } = installNotification('default');
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    // Merely receiving messages must never trigger a permission dialog.
    rerender(options({ messages: [message(THEM, 'hello')] }));
    expect(requestPermission).not.toHaveBeenCalled();

    await act(async () => {
      await result.current.requestDesktop();
    });
    expect(requestPermission).toHaveBeenCalledOnce();
  });

  it('mute stops the alerts without stopping the messages', () => {
    const { constructed } = installNotification('granted');
    const { result, rerender } = renderHook((props) => useSessionNotifications(props), {
      initialProps: options(),
    });

    act(() => result.current.toggleMuted());
    rerender(options({ messages: [message(THEM, 'hello')] }));

    expect(result.current.muted).toBe(true);
    expect(constructed).toHaveLength(0);
    expect(document.title).toBe('IntelliLect');
    // The message still arrived and is still unread — mute silences the alert, not the chat.
    expect(result.current.unreadChat).toBe(1);
  });
});
