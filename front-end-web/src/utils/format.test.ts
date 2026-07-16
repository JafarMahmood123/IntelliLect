import { describe, expect, it } from 'vitest';
import { formatBytes, formatDuration } from './format';

describe('formatDuration', () => {
  it('formats sub-hour durations as m:ss', () => {
    expect(formatDuration(0)).toBe('0:00');
    expect(formatDuration(5)).toBe('0:05');
    expect(formatDuration(65)).toBe('1:05');
    expect(formatDuration(754)).toBe('12:34');
  });

  it('formats hour+ durations as h:mm:ss', () => {
    expect(formatDuration(3723)).toBe('1:02:03');
  });

  it('handles invalid input safely', () => {
    expect(formatDuration(-10)).toBe('0:00');
    expect(formatDuration(Number.NaN)).toBe('0:00');
  });
});

describe('formatBytes', () => {
  it('formats bytes into KB/MB/GB', () => {
    expect(formatBytes(0)).toBe('0 KB');
    expect(formatBytes(2048)).toBe('2 KB');
    expect(formatBytes(1.5 * 1024 * 1024)).toBe('1.5 MB');
    expect(formatBytes(3 * 1024 * 1024 * 1024)).toBe('3 GB');
  });
});
