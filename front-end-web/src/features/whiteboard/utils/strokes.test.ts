import { describe, expect, it } from 'vitest';
import { EMPTY_BOARD } from '../types';
import type { BoardState, DrawTool, Stroke } from '../types';
import {
  applyMessage,
  distanceToSegment,
  hitsStroke,
  pointsToWire,
  shouldKeepPoint,
  strokesAt,
  wireToPoints,
} from './strokes';

const stroke = (
  id: string,
  points: number[][],
  tool: DrawTool = 'pen',
  text?: string,
): Stroke => ({
  id,
  tool,
  color: '#ef4444',
  width: 0.006,
  points: points.map(([x, y]) => ({ x, y })),
  ...(text ? { text } : {}),
});

const board = (...strokes: Stroke[]): BoardState => ({ ...EMPTY_BOARD, strokes });

describe('point encoding', () => {
  it('round-trips through the flat wire form', () => {
    const points = [
      { x: 0.1234, y: 0.5678 },
      { x: 0.9, y: 0.1 },
    ];

    expect(wireToPoints(pointsToWire(points))).toEqual(points);
  });

  it('rounds to four decimals', () => {
    expect(pointsToWire([{ x: 0.123456789, y: 0.5 }])).toEqual([0.1235, 0.5]);
  });

  it('drops a trailing half-pair instead of inventing a NaN point', () => {
    expect(wireToPoints([0.1, 0.2, 0.3])).toEqual([{ x: 0.1, y: 0.2 }]);
  });
});

describe('shouldKeepPoint', () => {
  it('always keeps the first point', () => {
    expect(shouldKeepPoint(undefined, { x: 0.5, y: 0.5 }, 16 / 9)).toBe(true);
  });

  it('drops a sample the hand barely moved through', () => {
    expect(shouldKeepPoint({ x: 0.5, y: 0.5 }, { x: 0.5002, y: 0.5 }, 16 / 9)).toBe(false);
  });

  it('keeps a real movement', () => {
    expect(shouldKeepPoint({ x: 0.5, y: 0.5 }, { x: 0.52, y: 0.5 }, 16 / 9)).toBe(true);
  });
});

describe('distanceToSegment', () => {
  it('corrects for the squash in normalised space', () => {
    // 0.1 sideways on a 16:9 frame is nearly twice 0.1 downwards. Without the correction the
    // eraser would reach further horizontally than the cursor suggests.
    const a = { x: 0, y: 0 };
    const horizontal = distanceToSegment({ x: 0.1, y: 0 }, a, a, 16 / 9);
    const vertical = distanceToSegment({ x: 0, y: 0.1 }, a, a, 16 / 9);

    expect(horizontal).toBeCloseTo(0.1 * (16 / 9));
    expect(vertical).toBeCloseTo(0.1);
  });

  it('measures to the nearest point on the segment, not to its ends', () => {
    const distance = distanceToSegment({ x: 0.5, y: 0.2 }, { x: 0, y: 0 }, { x: 1, y: 0 }, 1);

    expect(distance).toBeCloseTo(0.2);
  });

  it('treats a one-point stroke as a dot', () => {
    const a = { x: 0.3, y: 0.3 };

    expect(distanceToSegment({ x: 0.3, y: 0.4 }, a, a, 1)).toBeCloseTo(0.1);
  });
});

describe('hitsStroke', () => {
  it('catches freehand ink near the line, not just near its samples', () => {
    const line = stroke('a', [
      [0.1, 0.5],
      [0.9, 0.5],
    ]);

    expect(hitsStroke(line, { x: 0.5, y: 0.51 }, 0.02, 1)).toBe(true);
    expect(hitsStroke(line, { x: 0.5, y: 0.7 }, 0.02, 1)).toBe(false);
  });

  it('catches a shape anywhere inside it', () => {
    // Demanding a hit on a 2px outline is a miserable way to erase a rectangle.
    const box = stroke(
      'a',
      [
        [0.2, 0.2],
        [0.8, 0.8],
      ],
      'rect',
    );

    expect(hitsStroke(box, { x: 0.5, y: 0.5 }, 0.01, 1)).toBe(true);
    expect(hitsStroke(box, { x: 0.05, y: 0.5 }, 0.01, 1)).toBe(false);
  });

  it('estimates a box around text from its length', () => {
    const label = stroke('a', [[0.1, 0.5]], 'text', 'Hello');

    expect(hitsStroke(label, { x: 0.11, y: 0.497 }, 0.005, 1)).toBe(true);
    expect(hitsStroke(label, { x: 0.6, y: 0.497 }, 0.005, 1)).toBe(false);
  });

  it('never matches a stroke with no points', () => {
    expect(hitsStroke({ ...stroke('a', []), points: [] }, { x: 0, y: 0 }, 1, 1)).toBe(false);
  });
});

describe('strokesAt', () => {
  it('returns what the eraser caught, topmost first', () => {
    const under = stroke('under', [
      [0.4, 0.5],
      [0.6, 0.5],
    ]);
    const over = stroke('over', [
      [0.4, 0.5],
      [0.6, 0.5],
    ]);

    expect(strokesAt([under, over], { x: 0.5, y: 0.5 }, 0.02, 1)).toEqual(['over', 'under']);
  });
});

describe('applyMessage', () => {
  it('starts and then extends a freehand stroke', () => {
    const started = applyMessage(EMPTY_BOARD, { t: 'begin', s: stroke('a', [[0.1, 0.1]]) });
    const extended = applyMessage(started, { t: 'point', id: 'a', p: [0.2, 0.2] });

    expect(extended.strokes[0].points).toEqual([
      { x: 0.1, y: 0.1 },
      { x: 0.2, y: 0.2 },
    ]);
  });

  it('drops points for a stroke it never saw begin', () => {
    // Without the stroke's tool, colour and width there is nothing to draw, and defaulting them
    // would paint something the teacher never made.
    const after = applyMessage(EMPTY_BOARD, { t: 'point', id: 'ghost', p: [0.2, 0.2] });

    expect(after.strokes).toEqual([]);
  });

  it('replaces rather than duplicates a resent complete stroke', () => {
    const first = applyMessage(EMPTY_BOARD, { t: 'stroke', s: stroke('a', [[0.1, 0.1]]) });
    const again = applyMessage(first, { t: 'stroke', s: stroke('a', [[0.9, 0.9]]) });

    expect(again.strokes).toHaveLength(1);
    expect(again.strokes[0].points).toEqual([{ x: 0.9, y: 0.9 }]);
  });

  it('erases by id', () => {
    const state = board(stroke('a', [[0, 0]]), stroke('b', [[0, 0]]));

    expect(applyMessage(state, { t: 'erase', ids: ['a'] }).strokes.map((s) => s.id)).toEqual(['b']);
  });

  it('erases the pieces of a stroke that was split to fit a packet', () => {
    // A late joiner receives an oversized stroke as `a~a`/`a~b`. An erase of `a` has to take
    // them too, or that one participant is left with ink nobody else can see.
    const state = board(stroke('a~a', [[0, 0]]), stroke('a~b', [[0, 0]]), stroke('b', [[0, 0]]));

    expect(applyMessage(state, { t: 'erase', ids: ['a'] }).strokes.map((s) => s.id)).toEqual(['b']);
  });

  it('replaces the board on the first sync chunk and appends on the rest', () => {
    const stale = board(stroke('old', [[0, 0]]));

    const first = applyMessage(stale, {
      t: 'sync',
      i: 0,
      n: 2,
      strokes: [stroke('a', [[0, 0]])],
      enabled: true,
      frozen: false,
    });
    const second = applyMessage(first, {
      t: 'sync',
      i: 1,
      n: 2,
      strokes: [stroke('b', [[0, 0]])],
      enabled: true,
      frozen: false,
    });

    expect(second.strokes.map((s) => s.id)).toEqual(['a', 'b']);
    expect(second.enabled).toBe(true);
  });

  it('clears a stale board when the incoming one is empty', () => {
    const stale = board(stroke('old', [[0, 0]]));

    const synced = applyMessage(stale, {
      t: 'sync',
      i: 0,
      n: 1,
      strokes: [],
      enabled: false,
      frozen: false,
    });

    expect(synced.strokes).toEqual([]);
  });

  it('thaws the screen when the whiteboard is closed', () => {
    // A paused video with no visible unfreeze button would strand the class looking at a still.
    const frozen: BoardState = { ...EMPTY_BOARD, enabled: true, frozen: true };

    expect(applyMessage(frozen, { t: 'mode', on: false })).toMatchObject({
      enabled: false,
      frozen: false,
    });
  });

  it('leaves the board alone for transient messages', () => {
    const state = board(stroke('a', [[0, 0]]));

    expect(applyMessage(state, { t: 'laser', p: [0.5, 0.5] })).toBe(state);
    expect(applyMessage(state, { t: 'hello' })).toBe(state);
  });
});
