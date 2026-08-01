import { useCallback, useEffect, useRef } from 'react';
import { useDataChannel, useRoomContext } from '@livekit/components-react';
import { ConnectionState } from 'livekit-client';
import type { BoardState, WireMessage } from '../types';
import { TOPIC, decode, encode, syncChunks } from '../utils/protocol';

interface Options {
  /** True for the teacher. Only the owner of the board answers a request for it. */
  canDraw: boolean;
  /** Applied to the local board. Already validated — anything unrecognised never gets here. */
  onMessage: (msg: WireMessage) => void;
  /** Read lazily, so the reply to a late joiner carries the board as it is at that moment. */
  boardRef: React.RefObject<BoardState>;
}

export interface BoardSender {
  (msg: WireMessage, options?: { reliable?: boolean; to?: string[] }): void;
}

/**
 * The whiteboard's transport.
 *
 * LiveKit's data channel rather than our SignalR hub: it is already open, already authenticated
 * by the room token, and it does not send every stroke on a round trip through our backend. The
 * whole feature therefore needs no server change at all.
 *
 * The one thing the channel does not give us is history — packets are not buffered, so a student
 * who joins two minutes in would see an empty board. They ask for it instead: a `hello` on
 * connect, answered by the teacher with the board addressed to that student alone. Asking is
 * more robust than having the teacher watch for arrivals, because it also covers the case where
 * it was the TEACHER who reconnected and so never saw anyone arrive.
 */
export const useBoardChannel = ({ canDraw, onMessage, boardRef }: Options) => {
  const room = useRoomContext();

  // Held in refs so the data-channel handler never closes over a stale board or a stale role.
  // Written after commit rather than during render: a render can be discarded, and a ref updated
  // by one that was would leave the handler acting on a board that never existed.
  const onMessageRef = useRef(onMessage);
  const canDrawRef = useRef(canDraw);
  useEffect(() => {
    onMessageRef.current = onMessage;
    canDrawRef.current = canDraw;
  });

  const sendRef = useRef<BoardSender | null>(null);

  const { send: rawSend } = useDataChannel(TOPIC, (received) => {
    const msg = decode(received.payload);
    if (!msg) return;

    if (msg.t === 'hello') {
      // Only the teacher owns a board worth handing out, and only to the participant that asked.
      const identity = received.from?.identity;
      if (canDrawRef.current && identity) {
        for (const chunk of syncChunks(boardRef.current)) {
          sendRef.current?.(chunk, { to: [identity] });
        }
      }
      return;
    }

    onMessageRef.current(msg);
  });

  const send = useCallback<BoardSender>(
    (msg, options) => {
      // Publishing before the room is up rejects, and a stroke drawn during a reconnect is not
      // worth an unhandled rejection in the console — it is worth dropping.
      if (room.state !== ConnectionState.Connected) return;

      void rawSend(encode(msg), {
        topic: TOPIC,
        reliable: options?.reliable ?? true,
        ...(options?.to ? { destinationIdentities: options.to } : {}),
      }).catch(() => {
        // The channel closed mid-stroke. The board is replicated, not authoritative, and the
        // next sync repairs it; failing loudly here would only interrupt the lesson.
      });
    },
    [room, rawSend],
  );

  // `send` cannot exist before the handler that uses it — both come out of the same
  // `useDataChannel` call — so the handler reaches it through a ref, filled in after commit.
  useEffect(() => {
    sendRef.current = send;
  });

  // Students ask for the board once they are connected.
  useEffect(() => {
    if (canDraw || room.state !== ConnectionState.Connected) return;
    send({ t: 'hello' });
  }, [canDraw, room.state, send]);

  /** Hand the whole board to everyone. Used when the teacher opens the whiteboard. */
  const broadcastBoard = useCallback(() => {
    for (const chunk of syncChunks(boardRef.current)) send(chunk);
  }, [boardRef, send]);

  return { send, broadcastBoard };
};
