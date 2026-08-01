import type { BoardState, DrawTool, Point, Stroke, WireMessage } from '../types';
import { isFreehand } from '../types';

/** Coordinates go over the wire at 4 decimals — a ten-thousandth of a slide is sub-pixel at 4K. */
const round4 = (n: number): number => Math.round(n * 10_000) / 10_000;

export const newStrokeId = (): string =>
  globalThis.crypto?.randomUUID?.() ??
  `s-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

export const createStroke = (
  tool: DrawTool,
  color: string,
  width: number,
  first: Point,
  text?: string,
): Stroke => ({ id: newStrokeId(), tool, color, width, points: [first], ...(text ? { text } : {}) });

/** Points travel flat — [x, y, x, y, …] — which is a third fewer bytes than an array of objects. */
export const pointsToWire = (points: Point[]): number[] =>
  points.flatMap((p) => [round4(p.x), round4(p.y)]);

export const wireToPoints = (flat: number[]): Point[] => {
  const points: Point[] = [];
  // Floor, not round: a truncated packet with an odd length drops the half-pair rather than
  // inventing a point at NaN, which would poison every later distance calculation.
  for (let i = 0; i + 1 < flat.length; i += 2) points.push({ x: flat[i], y: flat[i + 1] });
  return points;
};

/**
 * Drop points the hand barely moved through.
 *
 * A pointer emits events far faster than anyone draws, and the surplus is pure cost: bytes on the
 * wire, and a jagged line where the noise between samples is larger than the movement.
 */
export const shouldKeepPoint = (last: Point | undefined, next: Point, aspect: number): boolean => {
  if (!last) return true;
  const dx = (next.x - last.x) * aspect;
  const dy = next.y - last.y;
  return Math.hypot(dx, dy) >= 0.002;
};

/** Distance from `p` to the segment `a`–`b`, measured in aspect-corrected board units. */
export const distanceToSegment = (p: Point, a: Point, b: Point, aspect: number): number => {
  const ax = a.x * aspect;
  const bx = b.x * aspect;
  const px = p.x * aspect;

  const dx = bx - ax;
  const dy = b.y - a.y;
  const lengthSquared = dx * dx + dy * dy;

  // A degenerate segment is a dot — a single-point stroke, or two samples at the same place.
  if (lengthSquared === 0) return Math.hypot(px - ax, p.y - a.y);

  const t = Math.max(0, Math.min(1, ((px - ax) * dx + (p.y - a.y) * dy) / lengthSquared));
  return Math.hypot(px - (ax + t * dx), p.y - (a.y + t * dy));
};

/**
 * The box a stroke occupies, in board units.
 *
 * Text is estimated rather than measured: measuring needs a canvas context, which would drag the
 * whole eraser into the browser and out of reach of a test. Half the font size per character is
 * close enough for a proportional face, and erasing is forgiving — the cost of being wrong is a
 * click that misses, not a corrupted board.
 */
const boundsOf = (stroke: Stroke): { x0: number; y0: number; x1: number; y1: number } => {
  if (stroke.tool === 'text') {
    const [anchor] = stroke.points;
    const width = stroke.width * 0.5 * (stroke.text?.length ?? 0);
    return { x0: anchor.x, y0: anchor.y - stroke.width, x1: anchor.x + width, y1: anchor.y };
  }

  const xs = stroke.points.map((p) => p.x);
  const ys = stroke.points.map((p) => p.y);
  return { x0: Math.min(...xs), y0: Math.min(...ys), x1: Math.max(...xs), y1: Math.max(...ys) };
};

/**
 * Whether the eraser at `p` catches this stroke.
 *
 * Freehand is caught by running near the ink. Shapes and text are caught anywhere inside them,
 * which is not geometrically pure — you can erase a rectangle by clicking its empty middle — but
 * it is what people expect, and demanding a hit on a 2px outline is a miserable way to erase.
 */
export const hitsStroke = (stroke: Stroke, p: Point, radius: number, aspect: number): boolean => {
  if (stroke.points.length === 0) return false;

  if (isFreehand(stroke.tool)) {
    if (stroke.points.length === 1) {
      return distanceToSegment(p, stroke.points[0], stroke.points[0], aspect) <= radius;
    }
    return stroke.points.some(
      (point, i) => i > 0 && distanceToSegment(p, stroke.points[i - 1], point, aspect) <= radius,
    );
  }

  const { x0, y0, x1, y1 } = boundsOf(stroke);
  const padX = radius / aspect;
  return p.x >= x0 - padX && p.x <= x1 + padX && p.y >= y0 - radius && p.y <= y1 + radius;
};

/** Ids of every stroke the eraser catches, newest first so the top one goes first. */
export const strokesAt = (
  strokes: Stroke[],
  p: Point,
  radius: number,
  aspect: number,
): string[] =>
  strokes
    .filter((stroke) => hitsStroke(stroke, p, radius, aspect))
    .map((stroke) => stroke.id)
    .reverse();

/**
 * The whole board, as a function of what has arrived.
 *
 * Deliberately pure and deliberately total: this runs on messages from the network, so an
 * unrecognised or out-of-order one has to leave the board alone rather than throw inside a render.
 */
export const applyMessage = (state: BoardState, msg: WireMessage): BoardState => {
  switch (msg.t) {
    case 'begin':
      return { ...state, strokes: [...state.strokes, msg.s] };

    case 'point': {
      const extra = wireToPoints(msg.p);
      if (extra.length === 0) return state;
      // A 'point' for a stroke we never saw begin is dropped, not resurrected: without its tool,
      // colour and width there is nothing to draw, and inventing defaults would paint a lie.
      if (!state.strokes.some((s) => s.id === msg.id)) return state;
      return {
        ...state,
        strokes: state.strokes.map((s) =>
          s.id === msg.id ? { ...s, points: [...s.points, ...extra] } : s,
        ),
      };
    }

    case 'stroke':
      // Replace-then-append, so a resend (or a shape the teacher adjusted) cannot duplicate it.
      return { ...state, strokes: [...state.strokes.filter((s) => s.id !== msg.s.id), msg.s] };

    case 'erase': {
      const gone = new Set(msg.ids);
      // A stroke too long for one packet is handed to a late joiner as `<id>~a`, `<id>~b`… so an
      // erase of the original must take its pieces with it, or the joiner alone keeps the ink.
      const isGone = (id: string) =>
        gone.has(id) || (id.includes('~') && gone.has(id.slice(0, id.indexOf('~'))));
      return { ...state, strokes: state.strokes.filter((s) => !isGone(s.id)) };
    }

    case 'clear':
      return { ...state, strokes: [] };

    case 'sync':
      // Chunk 0 replaces so a resync cannot double the board; later chunks extend it.
      return {
        ...state,
        strokes: msg.i === 0 ? msg.strokes : [...state.strokes, ...msg.strokes],
        enabled: msg.enabled,
        frozen: msg.frozen,
      };

    case 'freeze':
      return { ...state, frozen: msg.on };

    case 'mode':
      // Closing the whiteboard also lets the screen go: a paused video with no way to reach the
      // unfreeze button would strand the class looking at a still.
      return msg.on ? { ...state, enabled: true } : { ...state, enabled: false, frozen: false };

    // Laser is transient and never part of the board; hello is a request, not a change.
    case 'laser':
    case 'hello':
      return state;
  }
};
