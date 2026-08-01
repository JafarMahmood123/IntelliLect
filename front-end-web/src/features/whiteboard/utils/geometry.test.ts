import { describe, expect, it } from 'vitest';
import { aspectOf, contentRect, sameRect, toNormalised, toPixels } from './geometry';

describe('contentRect', () => {
  it('pillarboxes a picture narrower than its box', () => {
    // 16:9 source in a 4:3 box: full width is impossible, so it fits by height and centres.
    const rect = contentRect({ width: 1000, height: 1000 }, { width: 1920, height: 1080 })!;

    expect(rect.width).toBeCloseTo(1000);
    expect(rect.height).toBeCloseTo(562.5);
    expect(rect.left).toBeCloseTo(0);
    expect(rect.top).toBeCloseTo(218.75);
  });

  it('letterboxes a picture taller than its box', () => {
    const rect = contentRect({ width: 1000, height: 200 }, { width: 1920, height: 1080 })!;

    expect(rect.height).toBeCloseTo(200);
    expect(rect.width).toBeCloseTo(355.5555, 3);
    expect(rect.top).toBeCloseTo(0);
    expect(rect.left).toBeCloseTo(322.222, 2);
  });

  it('fills the box exactly when the aspects match', () => {
    const rect = contentRect({ width: 640, height: 360 }, { width: 1920, height: 1080 })!;

    expect(rect).toEqual({ left: 0, top: 0, width: 640, height: 360 });
  });

  it('is null before anything has been measured', () => {
    // A video reports 0x0 until metadata loads and a box is 0x0 on the first frame. Drawing
    // against either would divide by zero and put every stroke at NaN.
    expect(contentRect({ width: 0, height: 0 }, { width: 1920, height: 1080 })).toBeNull();
    expect(contentRect({ width: 800, height: 600 }, { width: 0, height: 0 })).toBeNull();
    expect(contentRect({ width: 800, height: -1 }, { width: 1920, height: 1080 })).toBeNull();
  });
});

describe('normalising', () => {
  const rect = { left: 100, top: 50, width: 800, height: 450 };

  it('round-trips a point back to where it started', () => {
    const back = toPixels(toNormalised(200, 300, rect), rect);

    expect(back.x).toBeCloseTo(200);
    expect(back.y).toBeCloseTo(300);
  });

  it('puts the same normalised point at the same place in a differently sized box', () => {
    // This is the whole reason for normalising: the teacher's box and the student's are not the
    // same size, and 40% across the slide has to mean the same thing in both.
    const teacher = { left: 0, top: 0, width: 800, height: 450 };
    const student = { left: 0, top: 0, width: 400, height: 225 };

    const drawn = toNormalised(320, 90, teacher);

    expect(drawn.x).toBeCloseTo(0.4);
    expect(toPixels(drawn, student).x).toBeCloseTo(160);
    expect(toPixels(drawn, student).y).toBeCloseTo(45);
  });

  it('reports zero rather than infinity for an unmeasured rect', () => {
    expect(toNormalised(10, 10, { left: 0, top: 0, width: 0, height: 0 })).toEqual({ x: 0, y: 0 });
  });
});

describe('aspectOf', () => {
  it('gives the width-to-height ratio', () => {
    expect(aspectOf({ left: 0, top: 0, width: 1600, height: 900 })).toBeCloseTo(16 / 9);
  });

  it('falls back to square rather than dividing by zero', () => {
    expect(aspectOf({ left: 0, top: 0, width: 100, height: 0 })).toBe(1);
  });
});

describe('sameRect', () => {
  const rect = { left: 0, top: 0, width: 800, height: 450 };

  it('ignores the sub-pixel churn a ResizeObserver reports on every layout settle', () => {
    expect(sameRect(rect, { ...rect, width: 800.4 })).toBe(true);
  });

  it('notices a real resize', () => {
    expect(sameRect(rect, { ...rect, width: 802 })).toBe(false);
  });

  it('treats null as its own value', () => {
    expect(sameRect(null, null)).toBe(true);
    expect(sameRect(null, rect)).toBe(false);
  });
});
