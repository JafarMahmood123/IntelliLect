import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReceivedDataMessage } from '@livekit/components-core';
import { WhiteboardProvider } from './WhiteboardProvider';
import { useWhiteboard } from '../context';
import type { WireMessage } from '../types';
import { decode, encode } from '../utils/protocol';

const mocks = vi.hoisted(() => ({
  send: vi.fn(() => Promise.resolve()),
  handler: { current: null as ((msg: unknown) => void) | null },
}));

// Only the two hooks the whiteboard actually uses. Standing up a real LiveKit room in jsdom would
// test the SDK rather than the board.
vi.mock('@livekit/components-react', async () => {
  const { ConnectionState } = await import('livekit-client');
  return {
    useRoomContext: () => ({ state: ConnectionState.Connected }),
    useDataChannel: (_topic: string, onMessage: (msg: unknown) => void) => {
      mocks.handler.current = onMessage;
      return { send: mocks.send, isSending: false, message: undefined };
    },
  };
});

/** Every message published, in order, decoded back into its domain form. */
const published = (): WireMessage[] =>
  mocks.send.mock.calls
    .map(([payload]) => decode(payload as unknown as Uint8Array))
    .filter((msg): msg is WireMessage => msg !== null);

const publishedOfType = <T extends WireMessage['t']>(t: T) =>
  published().filter((msg): msg is Extract<WireMessage, { t: T }> => msg.t === t);

/** Options passed alongside the nth published message. */
const optionsFor = (index: number) => mocks.send.mock.calls[index]?.[1] as Record<string, unknown>;

const receive = (msg: WireMessage, from = 'student-1') =>
  act(() => {
    mocks.handler.current?.({
      payload: encode(msg),
      topic: 'wb',
      from: { identity: from },
    } as unknown as ReceivedDataMessage<'wb'>);
  });

const Harness = () => {
  const board = useWhiteboard();
  return (
    <div>
      <span data-testid="strokes">{board.strokes.length}</span>
      <span data-testid="laser">{board.laser ? 'on' : 'off'}</span>
      <span data-testid="enabled">{String(board.enabled)}</span>
      <span data-testid="frozen">{String(board.frozen)}</span>
      <button
        type="button"
        onClick={() => {
          board.beginDraw({ x: 0.1, y: 0.1 }, 16 / 9);
          board.extendDraw({ x: 0.6, y: 0.6 }, 16 / 9);
          board.endDraw();
        }}
      >
        draw
      </button>
      <button type="button" onClick={board.undo}>
        undo
      </button>
      <button type="button" onClick={board.toggleEnabled}>
        toggle
      </button>
      <button type="button" onClick={() => board.setTool('laser')}>
        laser tool
      </button>
      <button type="button" onClick={() => board.setTool('rect')}>
        rect tool
      </button>
    </div>
  );
};

const renderBoard = (canDraw: boolean) =>
  render(
    <WhiteboardProvider canDraw={canDraw}>
      <Harness />
    </WhiteboardProvider>,
  );

describe('WhiteboardProvider', () => {
  afterEach(() => {
    mocks.send.mockClear();
    mocks.handler.current = null;
  });

  it('publishes a freehand stroke and shows it locally at the same time', async () => {
    const user = userEvent.setup();
    renderBoard(true);

    await user.click(screen.getByRole('button', { name: 'draw' }));

    // Begin carries everything needed to draw it; the points follow in a batch.
    const [begun] = publishedOfType('begin');
    expect(begun.s).toMatchObject({ tool: 'pen', points: [{ x: 0.1, y: 0.1 }] });
    expect(publishedOfType('point')[0].p).toEqual([0.6, 0.6]);

    // The teacher never waits on the network to see their own ink.
    expect(screen.getByTestId('strokes')).toHaveTextContent('1');
  });

  it('sends a complete shape once, on release, rather than streaming the drag', async () => {
    const user = userEvent.setup();
    renderBoard(true);

    await user.click(screen.getByRole('button', { name: 'rect tool' }));
    await user.click(screen.getByRole('button', { name: 'draw' }));

    expect(publishedOfType('begin')).toHaveLength(0);
    expect(publishedOfType('stroke')).toHaveLength(1);
    expect(publishedOfType('stroke')[0].s).toMatchObject({
      tool: 'rect',
      points: [
        { x: 0.1, y: 0.1 },
        { x: 0.6, y: 0.6 },
      ],
    });
  });

  it('asks for the board when a student joins', () => {
    // Data packets are not buffered, so a student who joins mid-lesson would otherwise sit in
    // front of a board they cannot see.
    renderBoard(false);

    expect(publishedOfType('hello')).toHaveLength(1);
  });

  it('does not ask for a board it owns', () => {
    renderBoard(true);

    expect(publishedOfType('hello')).toHaveLength(0);
  });

  it('answers a student’s request with the board, addressed to that student alone', async () => {
    const user = userEvent.setup();
    renderBoard(true);
    await user.click(screen.getByRole('button', { name: 'draw' }));
    mocks.send.mockClear();

    receive({ t: 'hello' }, 'student-7');

    const syncs = publishedOfType('sync');
    expect(syncs).toHaveLength(1);
    expect(syncs[0].strokes).toHaveLength(1);
    // Everyone else is already holding this board; sending it to the room would be waste.
    expect(optionsFor(0).destinationIdentities).toEqual(['student-7']);
  });

  it('renders a stroke drawn by someone else', () => {
    renderBoard(false);

    receive({
      t: 'begin',
      s: {
        id: 'remote-1',
        tool: 'pen',
        color: '#ef4444',
        width: 0.006,
        points: [{ x: 0.2, y: 0.2 }],
      },
    });

    expect(screen.getByTestId('strokes')).toHaveTextContent('1');
  });

  it('ignores a payload it cannot make sense of', () => {
    renderBoard(false);

    act(() => {
      mocks.handler.current?.({
        payload: new TextEncoder().encode('{"t":"begin","s":{"id":"x"}}'),
        topic: 'wb',
      } as unknown as ReceivedDataMessage<'wb'>);
    });

    expect(screen.getByTestId('strokes')).toHaveTextContent('0');
  });

  it('sends the laser lossy and never puts it on the board', async () => {
    const user = userEvent.setup();
    renderBoard(true);

    await user.click(screen.getByRole('button', { name: 'laser tool' }));
    await user.click(screen.getByRole('button', { name: 'draw' }));

    expect(publishedOfType('laser')).toHaveLength(1);
    expect(publishedOfType('begin')).toHaveLength(0);
    expect(screen.getByTestId('strokes')).toHaveTextContent('0');
    expect(screen.getByTestId('laser')).toHaveTextContent('on');

    const laserCall = mocks.send.mock.calls.findIndex(
      ([payload]) => decode(payload as unknown as Uint8Array)?.t === 'laser',
    );
    expect(optionsFor(laserCall).reliable).toBe(false);
  });

  it('undoes the teacher’s own last mark', async () => {
    const user = userEvent.setup();
    renderBoard(true);
    await user.click(screen.getByRole('button', { name: 'draw' }));

    await user.click(screen.getByRole('button', { name: 'undo' }));

    expect(publishedOfType('erase')).toHaveLength(1);
    expect(screen.getByTestId('strokes')).toHaveTextContent('0');
  });

  it('has nothing to undo before anything is drawn', async () => {
    const user = userEvent.setup();
    renderBoard(true);

    await user.click(screen.getByRole('button', { name: 'undo' }));

    expect(publishedOfType('erase')).toHaveLength(0);
  });

  it('follows the teacher in and out of the whiteboard', async () => {
    const user = userEvent.setup();
    renderBoard(true);

    await user.click(screen.getByRole('button', { name: 'toggle' }));

    expect(publishedOfType('mode')[0].on).toBe(true);
    expect(screen.getByTestId('enabled')).toHaveTextContent('true');
  });

  it('thaws the screen when the whiteboard closes', () => {
    renderBoard(false);

    receive({ t: 'mode', on: true });
    receive({ t: 'freeze', on: true });
    expect(screen.getByTestId('frozen')).toHaveTextContent('true');

    receive({ t: 'mode', on: false });

    // Otherwise the class is left staring at a paused slide with no control to resume it.
    expect(screen.getByTestId('frozen')).toHaveTextContent('false');
  });

  it('a student cannot draw even if the surface is reached', async () => {
    const user = userEvent.setup();
    renderBoard(false);
    mocks.send.mockClear();

    await user.click(screen.getByRole('button', { name: 'draw' }));

    expect(published()).toHaveLength(0);
    expect(screen.getByTestId('strokes')).toHaveTextContent('0');
  });
});
