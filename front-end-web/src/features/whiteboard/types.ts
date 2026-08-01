/**
 * Whiteboard domain types.
 *
 * COORDINATES: every point on the board is normalised 0…1 against the video's CONTENT rectangle
 * — the part of the box the picture actually occupies once `object-contain` has letterboxed it.
 * Screen pixels would be useless on the wire: the same shared screen is rendered at a different
 * size in every participant's browser, so a circle drawn around a word would land somewhere else
 * on every student's display. Normalised means "35% across the slide", which is true everywhere.
 *
 * WIDTHS are a fraction of the content rect's HEIGHT rather than a pixel count, for the same
 * reason: a 3px pen is a bold marker on a phone and a hairline on a 4K monitor.
 */

/** A point on the board, normalised 0…1 against the content rectangle. */
export interface Point {
  x: number;
  y: number;
}

/** Where the picture actually sits inside its box, in CSS pixels relative to that box. */
export interface ContentRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

/** Tools that leave ink behind. */
export type DrawTool = 'pen' | 'highlighter' | 'arrow' | 'rect' | 'ellipse' | 'line' | 'text';

/** Every tool in the box, including the two that never produce a stroke. */
export type ToolKind = DrawTool | 'eraser' | 'laser';

/**
 * Tools whose shape is defined by exactly two points — where the drag started and where it ended.
 * They are sent once, on release, rather than streamed: a rectangle that only exists when it is
 * finished is far less wire traffic than one that is redrawn on every mouse move, and a
 * half-dragged rectangle is not worth showing to the class anyway.
 */
const TWO_POINT_TOOLS = new Set<DrawTool>(['arrow', 'rect', 'ellipse', 'line']);

export const isTwoPoint = (tool: DrawTool): boolean => TWO_POINT_TOOLS.has(tool);

/** Pen and highlighter accumulate points as the hand moves, so they stream. */
export const isFreehand = (tool: DrawTool): boolean => tool === 'pen' || tool === 'highlighter';

export interface Stroke {
  id: string;
  tool: DrawTool;
  /** CSS colour. Sent verbatim, so it must stay short — the palette is all 7-character hex. */
  color: string;
  /** Fraction of the content rect's height. Doubles as the font size for the text tool. */
  width: number;
  /** Freehand: the whole path. Two-point tools: start and end. Text: a single anchor. */
  points: Point[];
  /** Text tool only. */
  text?: string;
}

/**
 * Everything the board looks like. Replicated to every participant; the teacher's copy is
 * authoritative, and a late joiner is given this wholesale rather than replaying history.
 */
export interface BoardState {
  strokes: Stroke[];
  /** Whether the teacher has the whiteboard open at all. */
  enabled: boolean;
  /** Whether the shared screen is paused so it can be annotated as a still. */
  frozen: boolean;
}

export const EMPTY_BOARD: BoardState = { strokes: [], enabled: false, frozen: false };

/**
 * The wire format.
 *
 * Kept terse deliberately — reliable data packets cap at 15 KiB, and while a single message is
 * nowhere near that, a board being handed to a late joiner easily is. Field names are one or two
 * characters for the same reason: at a few hundred points a second, `"points"` versus `"p"` is
 * real bandwidth.
 */
export type WireMessage =
  /** A freehand stroke has started. Carries its first point(s). */
  | { t: 'begin'; s: Stroke }
  /** More points for a freehand stroke already in flight. Flat [x, y, x, y, …]. */
  | { t: 'point'; id: string; p: number[] }
  /** A complete stroke — shapes and text, which are never streamed. */
  | { t: 'stroke'; s: Stroke }
  /** Remove these strokes. Undo is just an erase of one's own last stroke. */
  | { t: 'erase'; ids: string[] }
  | { t: 'clear' }
  /** "I have just joined, please send me the board." Answered only by the teacher. */
  | { t: 'hello' }
  /** The whole board, addressed to one participant. Chunked; chunk 0 replaces, the rest append. */
  | { t: 'sync'; i: number; n: number; strokes: Stroke[]; enabled: boolean; frozen: boolean }
  /** Transient pointer. Sent lossy — a laser dot that arrives late is worse than one that is lost. */
  | { t: 'laser'; p: number[] | null }
  /** Pause/resume the shared screen so a still can be annotated. */
  | { t: 'freeze'; on: boolean }
  /** The teacher opened or closed the whiteboard. */
  | { t: 'mode'; on: boolean };
