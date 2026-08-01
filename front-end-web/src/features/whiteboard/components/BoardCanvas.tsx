import { useEffect, useRef } from 'react';
import type { PointerEvent as ReactPointerEvent } from 'react';
import type { ContentRect, Point, Stroke } from '../types';
import { paintBoard } from '../utils/paint';

interface Props {
  rect: ContentRect;
  strokes: Stroke[];
  laser: Point | null;
  /** False for students and for a teacher with no tool in hand, so clicks reach what is beneath. */
  interactive: boolean;
  onPointerDown?: (e: ReactPointerEvent<HTMLCanvasElement>) => void;
  onPointerMove?: (e: ReactPointerEvent<HTMLCanvasElement>) => void;
  onPointerUp?: (e: ReactPointerEvent<HTMLCanvasElement>) => void;
  cursor?: string;
}

/**
 * The drawing surface, positioned exactly over the picture.
 *
 * It is sized to the content rectangle rather than to its container, which is what makes every
 * coordinate in the feature a plain fraction of this canvas — see `geometry.ts`.
 */
export const BoardCanvas = ({
  rect,
  strokes,
  laser,
  interactive,
  onPointerDown,
  onPointerMove,
  onPointerUp,
  cursor = 'crosshair',
}: Props) => {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;

    // The backing store is scaled by the display's pixel ratio and then scaled back down by the
    // transform. Without it every line is soft on a retina screen — which is exactly the screen
    // a teacher demonstrates on.
    const ratio = globalThis.devicePixelRatio || 1;
    canvas.width = Math.round(rect.width * ratio);
    canvas.height = Math.round(rect.height * ratio);

    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    paintBoard(ctx, rect, strokes, laser);
  }, [rect, strokes, laser]);

  return (
    <canvas
      ref={ref}
      data-testid="board-canvas"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      style={{
        position: 'absolute',
        left: rect.left,
        top: rect.top,
        width: rect.width,
        height: rect.height,
        // Without this a stylus or finger scrolls the page instead of drawing on it.
        touchAction: 'none',
        pointerEvents: interactive ? 'auto' : 'none',
        cursor,
      }}
    />
  );
};
