import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { RecorderPage } from './RecorderPage';

const mocks = vi.hoisted(() => ({
  connection: { current: 'disconnected' as string },
  participants: { current: [] as { identity: string; name: string; metadata?: string }[] },
  tracks: { current: [] as unknown[] },
}));

// The whole SDK is stubbed. Note there is deliberately NO `useParticipants` here: the recorder
// joins hidden but can still see itself, so reaching for it would put an empty tile of the robot
// into every recording. Leaving it out means that mistake fails the test rather than shipping.
vi.mock('@livekit/components-react', async () => {
  const { forwardRef } = await import('react');
  return {
    LiveKitRoom: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    RoomAudioRenderer: () => null,
    VideoTrack: forwardRef<HTMLVideoElement>((_props, ref) => (
      <video ref={ref} data-testid="screen-share" />
    )),
    ConnectionQualityIndicator: () => null,
    isTrackReference: (t: { publication?: unknown }) => Boolean(t?.publication),
    useConnectionState: () => mocks.connection.current,
    useRemoteParticipants: () => mocks.participants.current,
    useTracks: () => mocks.tracks.current,
    useIsSpeaking: () => false,
    useIsMuted: () => false,
    useRoomContext: () => ({ state: mocks.connection.current }),
    useDataChannel: () => ({ send: vi.fn(() => Promise.resolve()), isSending: false }),
  };
});

const atUrl = (search: string) => window.history.replaceState({}, '', `/recorder${search}`);

const participant = (identity: string) => ({ identity, name: identity, metadata: undefined });

describe('RecorderPage', () => {
  let log: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
    mocks.connection.current = 'disconnected';
    mocks.participants.current = [];
    mocks.tracks.current = [];
  });

  afterEach(() => {
    log.mockRestore();
    vi.clearAllMocks();
  });

  const logged = (signal: string) => log.mock.calls.filter(([arg]) => arg === signal).length;

  it('refuses to start without a url and token', () => {
    // LiveKit waits for START_RECORDING before it captures anything, so staying silent turns a
    // misconfigured template into a failed egress rather than an hour of blank video.
    atUrl('');

    render(<RecorderPage />);

    expect(screen.getByText(/Egress:CustomBaseUrl/)).toBeInTheDocument();
    expect(logged('START_RECORDING')).toBe(0);
  });

  it('signals the egress worker once the room is up', () => {
    atUrl('?url=ws://livekit:7880&token=abc');
    mocks.connection.current = 'connected';

    render(<RecorderPage />);

    expect(logged('START_RECORDING')).toBe(1);
  });

  it('does not signal before it has connected', () => {
    atUrl('?url=ws://livekit:7880&token=abc');
    mocks.connection.current = 'connecting';

    render(<RecorderPage />);

    expect(logged('START_RECORDING')).toBe(0);
  });

  it('signals the end when the room goes away underneath it', () => {
    atUrl('?url=ws://livekit:7880&token=abc');
    mocks.connection.current = 'connected';
    const { rerender } = render(<RecorderPage />);

    mocks.connection.current = 'disconnected';
    rerender(<RecorderPage />);

    expect(logged('END_RECORDING')).toBe(1);
  });

  it('records the people in the room, not itself', () => {
    atUrl('?url=ws://livekit:7880&token=abc');
    mocks.connection.current = 'connected';
    mocks.participants.current = [participant('teacher-1'), participant('student-1')];

    render(<RecorderPage />);

    // Exactly the remote participants — the hidden recorder contributes no tile of its own.
    expect(screen.getByText('teacher-1')).toBeInTheDocument();
    expect(screen.getByText('student-1')).toBeInTheDocument();
  });

  it('focuses a shared screen when there is one', () => {
    atUrl('?url=ws://livekit:7880&token=abc');
    mocks.connection.current = 'connected';
    mocks.participants.current = [participant('teacher-1')];
    mocks.tracks.current = [
      {
        source: 'screen_share',
        publication: { trackSid: 'sid-1' },
        participant: participant('teacher-1'),
      },
    ];

    render(<RecorderPage />);

    expect(screen.getByTestId('screen-share')).toBeInTheDocument();
  });
});
