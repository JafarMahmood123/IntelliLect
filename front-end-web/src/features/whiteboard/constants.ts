/**
 * Palette and sizes.
 *
 * Six colours, not a picker: a teacher mid-sentence needs one click, and every one of these has
 * to stay legible over a white slide AND over a dark code editor.
 */
export const PALETTE = [
  { name: 'Red', value: '#ef4444' },
  { name: 'Amber', value: '#f59e0b' },
  { name: 'Green', value: '#22c55e' },
  { name: 'Blue', value: '#3b82f6' },
  { name: 'White', value: '#ffffff' },
  { name: 'Black', value: '#0f172a' },
] as const;

/** Fractions of the picture's height, so a "medium" pen looks the same on a phone and a monitor. */
export const WIDTHS = [
  { name: 'Thin', value: 0.003 },
  { name: 'Medium', value: 0.006 },
  { name: 'Thick', value: 0.012 },
] as const;

export const DEFAULT_COLOR = PALETTE[0].value;
export const DEFAULT_WIDTH = WIDTHS[1].value;

/** Text at pen width would be unreadable, so the same setting means something larger here. */
export const TEXT_SCALE = 4;

/** How near the ink the eraser has to pass, as a fraction of height. Forgiving on purpose. */
export const ERASER_RADIUS = 0.02;

/** A laser dot outlives the hand that moved it by about this long, then fades. */
export const LASER_LINGER_MS = 1200;

/** Points are batched to the wire at this interval rather than one packet per pointer event. */
export const POINT_FLUSH_MS = 50;
