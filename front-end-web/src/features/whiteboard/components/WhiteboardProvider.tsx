import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { ConnectionState } from 'livekit-client';
import { useRoomContext } from '@livekit/components-react';
import { EMPTY_BOARD, isFreehand } from '../types';
import type { Point, Stroke, WireMessage } from '../types';
import { WhiteboardContext } from '../context';
import type { WhiteboardApi } from '../context';
import {
  DEFAULT_COLOR,
  DEFAULT_WIDTH,
  ERASER_RADIUS,
  LASER_LINGER_MS,
  POINT_FLUSH_MS,
  TEXT_SCALE,
} from '../constants';
import { applyMessage, createStroke, pointsToWire, shouldKeepPoint, strokesAt, wireToPoints } from '../utils/strokes';
import { useBoardChannel } from '../hooks/useBoardChannel';

/**
 * Owns the board and everything done to it.
 *
 * The one design decision worth knowing: a local action and a received one take EXACTLY the same
 * path. Drawing builds a `WireMessage`, feeds it to the same reducer a remote message goes
 * through, and then puts it on the wire. There is no separate "my strokes" code, so the teacher's
 * board and the students' cannot drift into disagreeing about what a message means.
 *
 * Must be mounted inside <LiveKitRoom> — the data channel needs the room context.
 */
export const WhiteboardProvider = ({
  canDraw,
  children,
}: {
  canDraw: boolean;
  children: ReactNode;
}) => {
  const room = useRoomContext();

  const [board, dispatch] = useReducer(applyMessage, EMPTY_BOARD);

  // Read by the data-channel handler and by the eraser, both of which run after a commit. Filled
  // in after render rather than during one, so a discarded render can never leave it describing
  // a board that was never shown.
  const boardRef = useRef(board);
  useEffect(() => {
    boardRef.current = board;
  });

  const [tool, setTool] = useState<WhiteboardApi['tool']>('pen');
  const [color, setColor] = useState<string>(DEFAULT_COLOR);
  const [width, setWidth] = useState<number>(DEFAULT_WIDTH);
  const [hidden, setHidden] = useState(false);

  /**
   * A two-point shape mid-drag. Local only until released — see `types.ts`.
   *
   * Mirrored into a ref because releasing it PUBLISHES it, and a side effect inside a state
   * updater runs twice under React 19's development double-invoke — which would put every
   * rectangle on the wire twice.
   */
  const [draft, setDraft] = useState<Stroke | null>(null);
  const draftRef = useRef<Stroke | null>(null);
  const putDraft = useCallback((stroke: Stroke | null) => {
    draftRef.current = stroke;
    setDraft(stroke);
  }, []);

  const [pendingText, setPendingText] = useState<Point | null>(null);
  const [laser, setLaser] = useState<Point | null>(null);

  /** Ids of the teacher's own marks, newest last. Same double-invoke reasoning as the draft. */
  const undoIds = useRef<string[]>([]);
  const [undoCount, setUndoCount] = useState(0);

  const laserTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const showLaser = useCallback((p: Point | null) => {
    setLaser(p);
    if (laserTimer.current) clearTimeout(laserTimer.current);
    if (p) laserTimer.current = setTimeout(() => setLaser(null), LASER_LINGER_MS);
  }, []);

  const handleMessage = useCallback(
    (msg: WireMessage) => {
      // Laser is the one thing that never belongs to the board: it is a gesture, not a mark.
      if (msg.t === 'laser') {
        showLaser(msg.p ? (wireToPoints(msg.p)[0] ?? null) : null);
        return;
      }
      dispatch(msg);
    },
    [showLaser],
  );

  const { send, broadcastBoard } = useBoardChannel({ canDraw, onMessage: handleMessage, boardRef });

  /** Apply locally and publish, in that order, so the teacher never waits on the network. */
  const perform = useCallback(
    (msg: WireMessage) => {
      dispatch(msg);
      send(msg);
    },
    [send],
  );

  // A teacher who reconnects comes back with an empty board while the class still holds the old
  // one. Publishing the (empty) board on connect realigns everyone rather than leaving strokes
  // on screen that the teacher can no longer erase.
  const hasRealigned = useRef(false);
  useEffect(() => {
    if (!canDraw || room.state !== ConnectionState.Connected || hasRealigned.current) return;
    hasRealigned.current = true;
    broadcastBoard();
  }, [canDraw, room.state, broadcastBoard]);

  // --- freehand streaming ----------------------------------------------------
  // Points reach the local canvas immediately and the wire in batches. One packet per pointer
  // event would be dozens a second per stroke for no visible gain.

  const liveId = useRef<string | null>(null);
  const pending = useRef<number[]>([]);
  const flushTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const flush = useCallback(() => {
    if (flushTimer.current) {
      clearTimeout(flushTimer.current);
      flushTimer.current = null;
    }
    if (!liveId.current || pending.current.length === 0) return;
    send({ t: 'point', id: liveId.current, p: pending.current });
    pending.current = [];
  }, [send]);

  useEffect(
    () => () => {
      if (flushTimer.current) clearTimeout(flushTimer.current);
      if (laserTimer.current) clearTimeout(laserTimer.current);
    },
    [],
  );

  const pushUndo = useCallback((id: string) => {
    undoIds.current = [...undoIds.current, id];
    setUndoCount(undoIds.current.length);
  }, []);

  const eraseAt = useCallback(
    (p: Point, aspect: number) => {
      // Topmost only: dragging the eraser then removes strokes one at a time, the way a real one
      // does, instead of wiping a whole cluster on first contact.
      const [top] = strokesAt(boardRef.current.strokes, p, ERASER_RADIUS, aspect);
      if (top) perform({ t: 'erase', ids: [top] });
    },
    [perform],
  );

  const lastLaserSent = useRef(0);
  const moveLaser = useCallback(
    (p: Point) => {
      showLaser(p);
      const now = Date.now();
      if (now - lastLaserSent.current < POINT_FLUSH_MS) return;
      lastLaserSent.current = now;
      // Lossy: a dot that arrives after the hand has moved on is worse than one that never came.
      send({ t: 'laser', p: pointsToWire([p]) }, { reliable: false });
    },
    [send, showLaser],
  );

  const beginDraw = useCallback(
    (p: Point, aspect: number) => {
      if (!canDraw) return;

      if (tool === 'eraser') {
        eraseAt(p, aspect);
        return;
      }
      if (tool === 'laser') {
        moveLaser(p);
        return;
      }
      if (tool === 'text') {
        setPendingText(p);
        return;
      }

      const stroke = createStroke(tool, color, width, p);
      if (isFreehand(tool)) {
        liveId.current = stroke.id;
        pending.current = [];
        perform({ t: 'begin', s: stroke });
        pushUndo(stroke.id);
      } else {
        // Shapes stay local until released, so the class is not shown a rectangle being dragged
        // out of nothing — and so a released shape is one packet rather than one per frame.
        putDraft(stroke);
      }
    },
    [canDraw, tool, color, width, eraseAt, moveLaser, perform, pushUndo, putDraft],
  );

  const extendDraw = useCallback(
    (p: Point, aspect: number) => {
      if (!canDraw) return;

      if (tool === 'laser') {
        moveLaser(p);
        return;
      }
      if (tool === 'eraser') {
        eraseAt(p, aspect);
        return;
      }

      if (liveId.current) {
        const live = boardRef.current.strokes.find((s) => s.id === liveId.current);
        const last = live?.points.at(-1);
        if (!shouldKeepPoint(last, p, aspect)) return;

        dispatch({ t: 'point', id: liveId.current, p: pointsToWire([p]) });
        pending.current.push(...pointsToWire([p]));
        if (!flushTimer.current) flushTimer.current = setTimeout(flush, POINT_FLUSH_MS);
        return;
      }

      // A shape is defined entirely by where it started and where the hand is now.
      const current = draftRef.current;
      if (current) putDraft({ ...current, points: [current.points[0], p] });
    },
    [canDraw, tool, eraseAt, moveLaser, flush, putDraft],
  );

  const endDraw = useCallback(() => {
    flush();
    liveId.current = null;

    const finished = draftRef.current;
    if (finished) {
      putDraft(null);
      perform({ t: 'stroke', s: finished });
      pushUndo(finished.id);
    }
  }, [flush, perform, pushUndo, putDraft]);

  const commitText = useCallback(
    (text: string) => {
      const anchor = pendingText;
      setPendingText(null);
      if (!anchor || text.trim().length === 0) return;

      const stroke = createStroke('text', color, width * TEXT_SCALE, anchor, text.trim());
      perform({ t: 'stroke', s: stroke });
      pushUndo(stroke.id);
    },
    [pendingText, color, width, perform, pushUndo],
  );

  const undo = useCallback(() => {
    // The teacher's OWN last mark, not the room's — undo should never reach across to something
    // they did not draw.
    const last = undoIds.current.at(-1);
    if (!last) return;
    undoIds.current = undoIds.current.slice(0, -1);
    setUndoCount(undoIds.current.length);
    perform({ t: 'erase', ids: [last] });
  }, [perform]);

  const clear = useCallback(() => {
    undoIds.current = [];
    setUndoCount(0);
    perform({ t: 'clear' });
  }, [perform]);

  const toggleEnabled = useCallback(() => {
    perform({ t: 'mode', on: !boardRef.current.enabled });
  }, [perform]);

  const setFrozen = useCallback((on: boolean) => perform({ t: 'freeze', on }), [perform]);

  const strokes = useMemo(
    () => (draft ? [...board.strokes, draft] : board.strokes),
    [board.strokes, draft],
  );

  const value = useMemo<WhiteboardApi>(
    () => ({
      enabled: board.enabled,
      frozen: board.frozen,
      hidden,
      canDraw,
      strokes,
      laser,
      tool,
      color,
      width,
      canUndo: undoCount > 0,
      pendingText,
      setTool,
      setColor,
      setWidth,
      setHidden,
      toggleEnabled,
      setFrozen,
      clear,
      undo,
      beginDraw,
      extendDraw,
      endDraw,
      commitText,
    }),
    [
      board.enabled,
      board.frozen,
      hidden,
      canDraw,
      strokes,
      laser,
      tool,
      color,
      width,
      undoCount,
      pendingText,
      toggleEnabled,
      setFrozen,
      clear,
      undo,
      beginDraw,
      extendDraw,
      endDraw,
      commitText,
    ],
  );

  return <WhiteboardContext.Provider value={value}>{children}</WhiteboardContext.Provider>;
};
