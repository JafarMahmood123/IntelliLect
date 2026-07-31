import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { formatCountdown, useQuizCountdown } from './useQuizCountdown';

afterEach(() => {
  vi.useRealTimers();
});

/** Pins the browser's clock so a deliberate skew against the server's can be asserted. */
const withDeviceClock = (deviceNow: string) => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date(deviceNow));
};

describe('useQuizCountdown', () => {
  it('counts down the remaining seconds', () => {
    withDeviceClock('2026-08-01T10:00:00Z');

    const { result } = renderHook(() =>
      useQuizCountdown('2026-08-01T10:05:00Z', '2026-08-01T10:00:00Z'),
    );

    expect(result.current).toBe(300);
  });

  it('uses the server clock, not the device clock', () => {
    // The device is five minutes fast. Counting down from its own clock would show 0 and lock the
    // student out of a quiz the server is still happily accepting answers for.
    withDeviceClock('2026-08-01T10:05:00Z');

    const { result } = renderHook(() =>
      useQuizCountdown('2026-08-01T10:05:00Z', '2026-08-01T10:00:00Z'),
    );

    expect(result.current).toBe(300);
  });

  it('is also correct when the device clock is slow', () => {
    withDeviceClock('2026-08-01T09:55:00Z');

    const { result } = renderHook(() =>
      useQuizCountdown('2026-08-01T10:05:00Z', '2026-08-01T10:00:00Z'),
    );

    expect(result.current).toBe(300);
  });

  it('never goes negative once the deadline has passed', () => {
    withDeviceClock('2026-08-01T10:00:00Z');

    const { result } = renderHook(() =>
      useQuizCountdown('2026-08-01T09:59:00Z', '2026-08-01T10:00:00Z'),
    );

    expect(result.current).toBe(0);
  });

  it('has no countdown for a quiz with no deadline', () => {
    const { result } = renderHook(() => useQuizCountdown(null, '2026-08-01T10:00:00Z'));

    expect(result.current).toBeNull();
  });
});

describe('formatCountdown', () => {
  it('renders mm:ss with a padded seconds field', () => {
    expect(formatCountdown(300)).toBe('5:00');
    expect(formatCountdown(65)).toBe('1:05');
    expect(formatCountdown(9)).toBe('0:09');
    expect(formatCountdown(0)).toBe('0:00');
  });

  it('renders a placeholder when there is nothing to count', () => {
    expect(formatCountdown(null)).toBe('--:--');
  });
});
