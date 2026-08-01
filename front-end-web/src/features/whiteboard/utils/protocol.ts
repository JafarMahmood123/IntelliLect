import type { BoardState, Stroke, WireMessage } from '../types';

/**
 * The wire.
 *
 * Everything here treats an incoming packet as hostile input. It arrives from another browser
 * over a channel any participant can publish on, and it is fed straight into a React render — so
 * a malformed message has to become `null` here rather than an exception three layers up.
 */

/** Data-channel topic. `useDataChannel` filters on this, so it must match on both sides. */
export const TOPIC = 'wb';

/**
 * Reliable data packets cap at 15 KiB including LiveKit's own headers. 12 KB leaves comfortable
 * room for those and for the JSON envelope, and being conservative here costs one extra packet
 * on a board handover — the only place the limit is ever approached.
 */
const MAX_PAYLOAD_BYTES = 12_000;

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export const encode = (msg: WireMessage): Uint8Array => encoder.encode(JSON.stringify(msg));

const byteLength = (msg: WireMessage): number => encode(msg).length;

const DRAW_TOOLS: ReadonlySet<string> = new Set([
  'pen',
  'highlighter',
  'arrow',
  'rect',
  'ellipse',
  'line',
  'text',
]);

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null;

const isFiniteNumberArray = (v: unknown): v is number[] =>
  Array.isArray(v) && v.every((n) => typeof n === 'number' && Number.isFinite(n));

const isStroke = (v: unknown): v is Stroke => {
  if (!isRecord(v)) return false;
  if (typeof v.id !== 'string' || v.id.length === 0) return false;
  if (typeof v.tool !== 'string' || !DRAW_TOOLS.has(v.tool)) return false;
  // Colour is written into a canvas context, never into markup, so an odd string is a no-op
  // rather than an injection — but a non-string would throw, and 32 characters is plenty for hex.
  if (typeof v.color !== 'string' || v.color.length > 32) return false;
  if (typeof v.width !== 'number' || !Number.isFinite(v.width) || v.width <= 0) return false;
  if (v.text !== undefined && typeof v.text !== 'string') return false;
  if (!Array.isArray(v.points) || v.points.length === 0) return false;
  return v.points.every(
    (p) =>
      isRecord(p) &&
      typeof p.x === 'number' &&
      typeof p.y === 'number' &&
      Number.isFinite(p.x) &&
      Number.isFinite(p.y),
  );
};

/** Narrows a parsed payload to a message we recognise, or null. */
const validate = (v: unknown): WireMessage | null => {
  if (!isRecord(v)) return null;

  switch (v.t) {
    case 'begin':
      return isStroke(v.s) ? { t: 'begin', s: v.s } : null;

    case 'point':
      return typeof v.id === 'string' && isFiniteNumberArray(v.p)
        ? { t: 'point', id: v.id, p: v.p }
        : null;

    case 'stroke':
      return isStroke(v.s) ? { t: 'stroke', s: v.s } : null;

    case 'erase':
      return Array.isArray(v.ids) && v.ids.every((id) => typeof id === 'string')
        ? { t: 'erase', ids: v.ids as string[] }
        : null;

    case 'clear':
      return { t: 'clear' };

    case 'hello':
      return { t: 'hello' };

    case 'sync':
      return typeof v.i === 'number' &&
        typeof v.n === 'number' &&
        typeof v.enabled === 'boolean' &&
        typeof v.frozen === 'boolean' &&
        Array.isArray(v.strokes) &&
        v.strokes.every(isStroke)
        ? {
            t: 'sync',
            i: v.i,
            n: v.n,
            strokes: v.strokes as Stroke[],
            enabled: v.enabled,
            frozen: v.frozen,
          }
        : null;

    case 'laser':
      if (v.p === null) return { t: 'laser', p: null };
      return isFiniteNumberArray(v.p) && v.p.length >= 2 ? { t: 'laser', p: v.p } : null;

    case 'freeze':
      return typeof v.on === 'boolean' ? { t: 'freeze', on: v.on } : null;

    case 'mode':
      return typeof v.on === 'boolean' ? { t: 'mode', on: v.on } : null;

    default:
      return null;
  }
};

/** Bytes off the channel → a message we trust, or null if it is anything else. */
export const decode = (bytes: Uint8Array): WireMessage | null => {
  try {
    return validate(JSON.parse(decoder.decode(bytes)));
  } catch {
    return null;
  }
};

/**
 * Halve a stroke until each piece fits in a packet.
 *
 * Only a very long unbroken scribble reaches this. The halves overlap by one point so they meet
 * without a visible gap, and their ids are derived from the original's so an erase can still find
 * them — see the `~` handling in `applyMessage`.
 */
const splitOversized = (stroke: Stroke): Stroke[] => {
  if (byteLength({ t: 'stroke', s: stroke }) <= MAX_PAYLOAD_BYTES) return [stroke];
  // Two points that still will not fit means something pathological (an enormous text run);
  // send it anyway rather than recurse forever. The channel will reject it, not us.
  if (stroke.points.length < 3) return [stroke];

  const mid = Math.ceil(stroke.points.length / 2);
  return [
    ...splitOversized({ ...stroke, id: `${stroke.id}~a`, points: stroke.points.slice(0, mid) }),
    ...splitOversized({ ...stroke, id: `${stroke.id}~b`, points: stroke.points.slice(mid - 1) }),
  ];
};

/**
 * The whole board, packed into as few packets as will hold it.
 *
 * Always returns at least one chunk, even for an empty board: chunk 0 REPLACES the receiver's
 * strokes, so an empty first chunk is how a joiner holding a stale board is told to drop it.
 */
export const syncChunks = (state: BoardState): WireMessage[] => {
  const envelope = byteLength({
    t: 'sync',
    i: 0,
    n: 0,
    strokes: [],
    enabled: state.enabled,
    frozen: state.frozen,
  });

  const groups: Stroke[][] = [];
  let current: Stroke[] = [];
  let size = envelope;

  for (const stroke of state.strokes.flatMap(splitOversized)) {
    const cost = byteLength({ t: 'stroke', s: stroke });
    if (current.length > 0 && size + cost > MAX_PAYLOAD_BYTES) {
      groups.push(current);
      current = [];
      size = envelope;
    }
    current.push(stroke);
    size += cost;
  }
  groups.push(current);

  return groups.map((strokes, i) => ({
    t: 'sync',
    i,
    n: groups.length,
    strokes,
    enabled: state.enabled,
    frozen: state.frozen,
  }));
};

/** Exposed for the test that guards the packet cap. */
export const payloadLimit = MAX_PAYLOAD_BYTES;
export const messageBytes = byteLength;
