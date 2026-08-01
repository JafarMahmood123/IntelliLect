import type { ContentRect, Point } from '../types';

/**
 * Fitting the drawing surface to the picture.
 *
 * The shared screen is rendered with `object-contain`, so it is letterboxed: centred inside its
 * box with bars on two sides. Drawing onto the whole box would mean every stroke needed the
 * letterbox subtracted at paint time, in both directions, on every client.
 *
 * Instead we compute the content rectangle ONCE and position the canvas exactly over it. After
 * that the canvas and the picture are the same surface, and normalising a pointer position is a
 * plain divide.
 */

/**
 * A blank whiteboard has no video to fit itself to, so it borrows a fixed 16:9 frame. This is
 * what lets both modes share one code path: the only difference between annotating a slide and
 * drawing on an empty board is where these numbers come from.
 */
export const BOARD_SOURCE = { width: 1920, height: 1080 } as const;

/**
 * Where an `object-contain` picture of `source` dimensions sits inside `box`, in CSS pixels
 * relative to the box's top-left.
 *
 * Null when either is unmeasurable — a video reports 0×0 until its metadata loads, and a box is
 * 0×0 for the first frame after mount. Callers draw nothing rather than dividing by zero.
 */
export const contentRect = (
  box: { width: number; height: number },
  source: { width: number; height: number },
): ContentRect | null => {
  if (box.width <= 0 || box.height <= 0 || source.width <= 0 || source.height <= 0) return null;

  const scale = Math.min(box.width / source.width, box.height / source.height);
  const width = source.width * scale;
  const height = source.height * scale;

  return {
    left: (box.width - width) / 2,
    top: (box.height - height) / 2,
    width,
    height,
  };
};

/** Canvas-relative pixels → normalised board coordinates. */
export const toNormalised = (x: number, y: number, rect: ContentRect): Point => ({
  x: rect.width > 0 ? x / rect.width : 0,
  y: rect.height > 0 ? y / rect.height : 0,
});

/** Normalised board coordinates → canvas-relative pixels. */
export const toPixels = (p: Point, rect: ContentRect): { x: number; y: number } => ({
  x: p.x * rect.width,
  y: p.y * rect.height,
});

/**
 * How much wider than tall the content rectangle is.
 *
 * Needed wherever a DISTANCE is measured — the eraser especially. Normalised space is squashed:
 * 0.1 across a 16:9 frame is nearly twice the distance that 0.1 down it is, so a naive hypotenuse
 * makes the eraser reach further sideways than it appears to.
 */
export const aspectOf = (rect: ContentRect): number =>
  rect.height > 0 ? rect.width / rect.height : 1;

/**
 * Whether two rectangles are the same to within a pixel.
 *
 * Guards the measurement effects: a ResizeObserver reports sub-pixel changes on any layout
 * settle, and storing each one would re-render the canvas forever without anything moving.
 */
export const sameRect = (a: ContentRect | null, b: ContentRect | null): boolean => {
  if (a === null || b === null) return a === b;
  return (
    Math.abs(a.left - b.left) < 1 &&
    Math.abs(a.top - b.top) < 1 &&
    Math.abs(a.width - b.width) < 1 &&
    Math.abs(a.height - b.height) < 1
  );
};
