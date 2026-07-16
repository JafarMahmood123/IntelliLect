import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RoomEvent } from 'livekit-client';
import { TeacherFeedbackPanel } from './TeacherFeedbackPanel';
import { useAuthStore } from '../../../store/useAuthStore';

// A minimal fake LiveKit Room: records on/off and can emit data packets.
class FakeRoom {
  private handlers: Record<string, Set<(payload: Uint8Array) => void>> = {};

  on = vi.fn((event: string, cb: (payload: Uint8Array) => void) => {
    (this.handlers[event] ??= new Set()).add(cb);
    return this;
  });

  off = vi.fn((event: string, cb: (payload: Uint8Array) => void) => {
    this.handlers[event]?.delete(cb);
    return this;
  });

  emitData(payload: Uint8Array) {
    this.handlers[RoomEvent.DataReceived]?.forEach((cb) => cb(payload));
  }

  listenerCount(event: string) {
    return this.handlers[event]?.size ?? 0;
  }
}

const { roomHolder } = vi.hoisted(() => ({
  roomHolder: { current: null as unknown as FakeRoom },
}));

vi.mock('@livekit/components-react', () => ({
  useRoomContext: () => roomHolder.current,
}));

const encode = (value: unknown): Uint8Array =>
  new TextEncoder().encode(
    typeof value === 'string' ? value : JSON.stringify(value),
  );

const makePayload = (overrides: Record<string, unknown> = {}) => ({
  type: 'teaching_suggestion',
  version: 1,
  session_id: 'sess-1',
  feedback_type: 'discrepancy',
  text: 'Slide 4 contradicts the latency claim.',
  sources: [
    { citation: 1, document_id: 'doc-1', page: null, slide: 4, section: null },
    { citation: 2, document_id: 'doc-1', page: 12, slide: null, section: null },
  ],
  created_at: '2026-01-01T10:00:00Z',
  ...overrides,
});

const setRole = (roleName: 'Teacher' | 'Student') => {
  useAuthStore.setState({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    user: { roleName } as any,
    isAuthenticated: true,
  });
};

const emit = async (payload: unknown) => {
  await act(async () => {
    roomHolder.current.emitData(encode(payload));
  });
};

describe('TeacherFeedbackPanel', () => {
  beforeEach(() => {
    roomHolder.current = new FakeRoom();
    setRole('Teacher');
  });

  afterEach(() => {
    cleanup();
    useAuthStore.setState({ user: null, isAuthenticated: false });
    vi.clearAllMocks();
  });

  it('mounts for a teacher and renders a received suggestion (text + type + sources)', async () => {
    render(<TeacherFeedbackPanel />);

    // Chrome resolves via i18n (no raw keys).
    expect(screen.getByText('Teaching assistant')).toBeInTheDocument();
    expect(screen.queryByText('feedback.title')).not.toBeInTheDocument();
    expect(screen.getByText('No suggestions yet')).toBeInTheDocument();

    await emit(makePayload());

    expect(
      screen.getByText('Slide 4 contradicts the latency claim.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Discrepancy')).toBeInTheDocument(); // feedback_type badge
    expect(screen.getByText('slide 4')).toBeInTheDocument();
    expect(screen.getByText('p. 12')).toBeInTheDocument();
  });

  it('does NOT mount for a student, and attaches no data listener', async () => {
    setRole('Student');
    const { container } = render(<TeacherFeedbackPanel />);

    expect(container).toBeEmptyDOMElement();
    // The listener must never be attached for a student.
    expect(roomHolder.current.on).not.toHaveBeenCalled();
    expect(roomHolder.current.listenerCount(RoomEvent.DataReceived)).toBe(0);

    // Even if a message somehow arrives, nothing is shown.
    await emit(makePayload());
    expect(
      screen.queryByText('Slide 4 contradicts the latency claim.'),
    ).not.toBeInTheDocument();
  });

  it('ignores wrong type, unknown version, and malformed payloads without crashing', async () => {
    render(<TeacherFeedbackPanel />);

    await emit(makePayload({ type: 'chat_message' }));
    await emit(makePayload({ version: 2 }));
    await emit('not-json{');

    expect(screen.getByText('No suggestions yet')).toBeInTheDocument();

    // A valid one still renders afterwards.
    await emit(makePayload({ text: 'Valid one.' }));
    expect(screen.getByText('Valid one.')).toBeInTheDocument();
  });

  it('dismisses a suggestion when its dismiss button is clicked', async () => {
    const user = userEvent.setup();
    render(<TeacherFeedbackPanel />);

    await emit(makePayload({ text: 'Dismiss me.' }));
    expect(screen.getByText('Dismiss me.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Dismiss suggestion' }));

    expect(screen.queryByText('Dismiss me.')).not.toBeInTheDocument();
    expect(screen.getByText('No suggestions yet')).toBeInTheDocument();
  });

  it('caps the visible list at the most recent 5, newest first', async () => {
    render(<TeacherFeedbackPanel />);

    for (let i = 1; i <= 7; i += 1) {
      await emit(makePayload({ text: `Suggestion ${i}` }));
    }

    // Oldest two dropped, newest five kept.
    expect(screen.queryByText('Suggestion 1')).not.toBeInTheDocument();
    expect(screen.queryByText('Suggestion 2')).not.toBeInTheDocument();
    expect(screen.getByText('Suggestion 3')).toBeInTheDocument();
    expect(screen.getByText('Suggestion 7')).toBeInTheDocument();
    expect(screen.getAllByText(/^Suggestion \d$/)).toHaveLength(5);
  });

  it('removes the data listener on unmount', () => {
    const { unmount } = render(<TeacherFeedbackPanel />);
    expect(roomHolder.current.listenerCount(RoomEvent.DataReceived)).toBe(1);

    unmount();

    expect(roomHolder.current.off).toHaveBeenCalledWith(
      RoomEvent.DataReceived,
      expect.any(Function),
    );
    expect(roomHolder.current.listenerCount(RoomEvent.DataReceived)).toBe(0);
  });
});
