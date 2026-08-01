import { useEffect, useState } from 'react';
import type { ContentRect } from '../types';
import { contentRect, sameRect } from '../utils/geometry';

/**
 * The shared screen's own dimensions, or null until they are known.
 *
 * Two events matter and both are easy to forget. `loadedmetadata` is when the numbers first
 * exist — before it the element honestly reports 0×0. `resize` fires when the TEACHER resizes
 * the window they are sharing, which changes the aspect ratio mid-lesson; without it the canvas
 * would keep fitting itself to a shape the picture no longer has.
 */
export const useVideoSize = (video: HTMLVideoElement | null) => {
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);

  useEffect(() => {
    if (!video) return;

    const measure = () => {
      const { videoWidth: width, videoHeight: height } = video;
      setSize((current) =>
        current?.width === width && current?.height === height ? current : { width, height },
      );
    };

    measure();
    video.addEventListener('loadedmetadata', measure);
    video.addEventListener('resize', measure);

    return () => {
      video.removeEventListener('loadedmetadata', measure);
      video.removeEventListener('resize', measure);
    };
  }, [video]);

  // Gated on `video` rather than cleared in the effect: a measurement belongs to the element it
  // came from, so when that element goes the answer is "unknown" without a second render to say so.
  if (!video || !size || size.width <= 0 || size.height <= 0) return null;
  return size;
};

/**
 * Where the picture sits inside `box`, tracked as either of them changes.
 *
 * Compared through `sameRect` before storing: a ResizeObserver reports sub-pixel deltas on every
 * layout settle, and committing each one would repaint the board continuously with nothing on
 * screen having moved.
 */
export const useContentRect = (
  box: HTMLElement | null,
  source: { width: number; height: number } | null,
): ContentRect | null => {
  const [rect, setRect] = useState<ContentRect | null>(null);

  useEffect(() => {
    if (!box || !source) return;

    const measure = () => {
      const next = contentRect({ width: box.clientWidth, height: box.clientHeight }, source);
      setRect((current) => (sameRect(current, next) ? current : next));
    };

    measure();

    const observer = new ResizeObserver(measure);
    observer.observe(box);

    return () => observer.disconnect();
  }, [box, source]);

  return box && source ? rect : null;
};
