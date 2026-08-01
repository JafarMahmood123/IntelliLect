import { describe, expect, it } from 'vitest';
import { EMPTY_BOARD } from '../types';
import type { Stroke } from '../types';
import { decode, encode, messageBytes, payloadLimit, syncChunks } from './protocol';

const stroke = (id: string, pointCount = 1): Stroke => ({
  id,
  tool: 'pen',
  color: '#ef4444',
  width: 0.006,
  points: Array.from({ length: pointCount }, (_, i) => ({
    x: (i % 100) / 100,
    y: (i % 97) / 97,
  })),
});

describe('encode / decode', () => {
  it('round-trips every message kind', () => {
    const messages = [
      { t: 'begin', s: stroke('a') },
      { t: 'point', id: 'a', p: [0.1, 0.2] },
      { t: 'stroke', s: stroke('b') },
      { t: 'erase', ids: ['a', 'b'] },
      { t: 'clear' },
      { t: 'hello' },
      { t: 'laser', p: [0.4, 0.6] },
      { t: 'laser', p: null },
      { t: 'freeze', on: true },
      { t: 'mode', on: false },
    ] as const;

    for (const msg of messages) expect(decode(encode(msg))).toEqual(msg);
  });
});

describe('decode rejects what it does not recognise', () => {
  const bytes = (v: unknown) => new TextEncoder().encode(JSON.stringify(v));

  it('returns null rather than throwing on rubbish', () => {
    // This runs on bytes any participant can publish, and its result is fed into a render.
    expect(decode(new TextEncoder().encode('not json at all'))).toBeNull();
    expect(decode(bytes(null))).toBeNull();
    expect(decode(bytes([1, 2, 3]))).toBeNull();
    expect(decode(bytes({ t: 'something-else' }))).toBeNull();
  });

  it('rejects a stroke missing the fields needed to draw it', () => {
    expect(decode(bytes({ t: 'begin', s: { id: 'a', tool: 'pen' } }))).toBeNull();
    expect(decode(bytes({ t: 'begin', s: { ...stroke('a'), tool: 'rootkit' } }))).toBeNull();
    expect(decode(bytes({ t: 'begin', s: { ...stroke('a'), points: [] } }))).toBeNull();
    expect(decode(bytes({ t: 'begin', s: { ...stroke('a'), width: 0 } }))).toBeNull();
  });

  it('rejects non-finite coordinates', () => {
    // JSON turns Infinity and NaN into null, which would otherwise reach the canvas as a
    // coordinate and silently break every later distance calculation.
    expect(decode(bytes({ t: 'point', id: 'a', p: [null, 0.5] }))).toBeNull();
    expect(decode(bytes({ t: 'begin', s: { ...stroke('a'), points: [{ x: 0, y: null }] } }))).toBeNull();
  });

  it('rejects a flag that is not a boolean', () => {
    expect(decode(bytes({ t: 'freeze', on: 'yes' }))).toBeNull();
    expect(decode(bytes({ t: 'mode', on: 1 }))).toBeNull();
  });
});

describe('syncChunks', () => {
  it('sends one chunk even for an empty board', () => {
    // Chunk 0 replaces, so this is how a joiner holding a stale board is told to drop it.
    const chunks = syncChunks(EMPTY_BOARD);

    expect(chunks).toHaveLength(1);
    expect(chunks[0]).toMatchObject({ t: 'sync', i: 0, n: 1, strokes: [] });
  });

  it('keeps every chunk inside the packet limit', () => {
    const state = { ...EMPTY_BOARD, strokes: Array.from({ length: 400 }, (_, i) => stroke(`s${i}`, 40)) };

    const chunks = syncChunks(state);

    expect(chunks.length).toBeGreaterThan(1);
    for (const chunk of chunks) expect(messageBytes(chunk)).toBeLessThanOrEqual(payloadLimit);
  });

  it('splits a single stroke too long for one packet', () => {
    const state = { ...EMPTY_BOARD, strokes: [stroke('huge', 4000)] };

    const chunks = syncChunks(state);
    const ids = chunks.flatMap((c) => (c.t === 'sync' ? c.strokes.map((s) => s.id) : []));

    expect(ids.length).toBeGreaterThan(1);
    // Ids stay derived from the original so an erase of `huge` still finds the pieces.
    expect(ids.every((id) => id.startsWith('huge'))).toBe(true);
    for (const chunk of chunks) expect(messageBytes(chunk)).toBeLessThanOrEqual(payloadLimit);
  });

  it('leaves no gap where a stroke was split', () => {
    const state = { ...EMPTY_BOARD, strokes: [stroke('huge', 4000)] };

    const pieces = syncChunks(state).flatMap((c) => (c.t === 'sync' ? c.strokes : []));

    // The halves overlap by a point, so the line meets rather than showing a notch.
    for (let i = 1; i < pieces.length; i += 1) {
      const previousEnd = pieces[i - 1].points.at(-1);
      expect(pieces[i].points[0]).toEqual(previousEnd);
    }
  });

  it('numbers the chunks so the receiver knows when it has them all', () => {
    const state = { ...EMPTY_BOARD, strokes: Array.from({ length: 300 }, (_, i) => stroke(`s${i}`, 40)) };

    const chunks = syncChunks(state);

    chunks.forEach((chunk, i) => expect(chunk).toMatchObject({ i, n: chunks.length }));
  });

  it('carries the board flags on every chunk', () => {
    const state = { ...EMPTY_BOARD, enabled: true, frozen: true, strokes: [stroke('a')] };

    for (const chunk of syncChunks(state)) {
      expect(chunk).toMatchObject({ enabled: true, frozen: true });
    }
  });
});
