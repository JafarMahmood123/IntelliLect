import type { ContentRect, Point, Stroke } from '../types';
import { toPixels } from './geometry';

/**
 * Painting. Deliberately the only module here that touches a canvas, and deliberately the only
 * one without tests — everything worth asserting (the fit, the wire format, the eraser) is pure
 * and lives elsewhere. What is left is drawing calls, which a test could only restate.
 */

/** Highlighter ink is translucent and fat, so it sits over text without hiding it. */
const HIGHLIGHTER_ALPHA = 0.35;
const HIGHLIGHTER_SCALE = 4;

const path = (ctx: CanvasRenderingContext2D, points: Point[], rect: ContentRect) => {
  const pixels = points.map((p) => toPixels(p, rect));
  ctx.beginPath();
  ctx.moveTo(pixels[0].x, pixels[0].y);

  if (pixels.length === 2) {
    ctx.lineTo(pixels[1].x, pixels[1].y);
    return;
  }

  // Curve through the midpoints rather than joining the samples directly: a polyline of pointer
  // events looks visibly faceted, and this costs one extra call per point to avoid that.
  for (let i = 1; i < pixels.length - 1; i += 1) {
    const midX = (pixels[i].x + pixels[i + 1].x) / 2;
    const midY = (pixels[i].y + pixels[i + 1].y) / 2;
    ctx.quadraticCurveTo(pixels[i].x, pixels[i].y, midX, midY);
  }
  ctx.lineTo(pixels[pixels.length - 1].x, pixels[pixels.length - 1].y);
};

const arrowHead = (
  ctx: CanvasRenderingContext2D,
  from: { x: number; y: number },
  to: { x: number; y: number },
  size: number,
) => {
  const angle = Math.atan2(to.y - from.y, to.x - from.x);
  const spread = Math.PI / 7;

  ctx.beginPath();
  ctx.moveTo(to.x, to.y);
  ctx.lineTo(to.x - size * Math.cos(angle - spread), to.y - size * Math.sin(angle - spread));
  ctx.moveTo(to.x, to.y);
  ctx.lineTo(to.x - size * Math.cos(angle + spread), to.y - size * Math.sin(angle + spread));
  ctx.stroke();
};

const paintStroke = (ctx: CanvasRenderingContext2D, stroke: Stroke, rect: ContentRect) => {
  if (stroke.points.length === 0) return;

  const width = Math.max(1, stroke.width * rect.height);

  ctx.save();
  ctx.strokeStyle = stroke.color;
  ctx.fillStyle = stroke.color;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.lineWidth = width;

  if (stroke.tool === 'highlighter') {
    ctx.globalAlpha = HIGHLIGHTER_ALPHA;
    ctx.lineWidth = width * HIGHLIGHTER_SCALE;
    ctx.lineCap = 'butt';
  }

  const first = toPixels(stroke.points[0], rect);
  const last = toPixels(stroke.points[stroke.points.length - 1], rect);

  switch (stroke.tool) {
    case 'pen':
    case 'highlighter':
      if (stroke.points.length === 1) {
        // A tap with no movement is still a mark; without this it would vanish.
        ctx.beginPath();
        ctx.arc(first.x, first.y, ctx.lineWidth / 2, 0, Math.PI * 2);
        ctx.fill();
      } else {
        path(ctx, stroke.points, rect);
        ctx.stroke();
      }
      break;

    case 'line':
      ctx.beginPath();
      ctx.moveTo(first.x, first.y);
      ctx.lineTo(last.x, last.y);
      ctx.stroke();
      break;

    case 'arrow':
      ctx.beginPath();
      ctx.moveTo(first.x, first.y);
      ctx.lineTo(last.x, last.y);
      ctx.stroke();
      arrowHead(ctx, first, last, Math.max(width * 4, 10));
      break;

    case 'rect':
      ctx.strokeRect(
        Math.min(first.x, last.x),
        Math.min(first.y, last.y),
        Math.abs(last.x - first.x),
        Math.abs(last.y - first.y),
      );
      break;

    case 'ellipse':
      ctx.beginPath();
      ctx.ellipse(
        (first.x + last.x) / 2,
        (first.y + last.y) / 2,
        Math.abs(last.x - first.x) / 2,
        Math.abs(last.y - first.y) / 2,
        0,
        0,
        Math.PI * 2,
      );
      ctx.stroke();
      break;

    case 'text': {
      const size = Math.max(10, stroke.width * rect.height);
      ctx.font = `600 ${size}px Inter, system-ui, sans-serif`;
      ctx.textBaseline = 'alphabetic';
      ctx.fillText(stroke.text ?? '', first.x, first.y);
      break;
    }
  }

  ctx.restore();
};

/** The transient pointer. Drawn last so it is never buried under ink. */
const paintLaser = (ctx: CanvasRenderingContext2D, laser: Point, rect: ContentRect) => {
  const { x, y } = toPixels(laser, rect);
  const radius = Math.max(4, rect.height * 0.008);

  ctx.save();
  ctx.globalAlpha = 0.25;
  ctx.fillStyle = '#ef4444';
  ctx.beginPath();
  ctx.arc(x, y, radius * 2.5, 0, Math.PI * 2);
  ctx.fill();

  ctx.globalAlpha = 1;
  ctx.beginPath();
  ctx.arc(x, y, radius, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();
};

/**
 * Repaint everything.
 *
 * A full repaint each frame rather than an incremental one: a lesson produces hundreds of
 * strokes, not hundreds of thousands, and keeping this stateless means an erase or an undo needs
 * no special case. If it ever drags, committed strokes move to an offscreen canvas and only the
 * in-progress stroke is redrawn — but there is no point paying that complexity in advance.
 */
export const paintBoard = (
  ctx: CanvasRenderingContext2D,
  rect: ContentRect,
  strokes: Stroke[],
  laser: Point | null,
) => {
  ctx.clearRect(0, 0, rect.width, rect.height);
  for (const stroke of strokes) paintStroke(ctx, stroke, rect);
  if (laser) paintLaser(ctx, laser, rect);
};
