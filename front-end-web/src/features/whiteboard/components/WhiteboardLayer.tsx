import { useEffect, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent } from 'react';
import { Eye, EyeOff } from 'lucide-react';
import { useContentRect, useVideoSize } from '../hooks/useContentRect';
import { BOARD_SOURCE, aspectOf, toNormalised, toPixels } from '../utils/geometry';
import { TEXT_SCALE } from '../constants';
import type { Point } from '../types';
import { BoardCanvas } from './BoardCanvas';
import { Toolbox } from './Toolbox';
import { useWhiteboard } from '../context';

interface Props {
  /** 'annotate' draws over the shared screen; 'board' draws on an empty 16:9 surface. */
  mode: 'annotate' | 'board';
  /** The shared screen's element, so the canvas can be fitted to it — and paused for a freeze. */
  video?: HTMLVideoElement | null;
}

/**
 * The drawing surface and its controls, sized to whatever is underneath.
 *
 * Fills its parent and positions the canvas over the picture's content rectangle. The layer
 * itself never blocks the pointer: only the canvas does, and only while the teacher has a tool
 * in hand — otherwise it would swallow clicks meant for the video controls beneath it.
 */
export const WhiteboardLayer = ({ mode, video = null }: Props) => {
  const board = useWhiteboard();
  const [box, setBox] = useState<HTMLDivElement | null>(null);

  const videoSize = useVideoSize(video);
  const source = mode === 'board' ? BOARD_SOURCE : videoSize;
  const rect = useContentRect(box, source);

  // Freezing pauses the element rather than shipping a still: a MediaStream-backed <video> holds
  // its last frame while paused and rejoins the live edge on play, so it costs nothing on the
  // wire and every participant freezes the same moment they were already watching.
  useEffect(() => {
    if (!video || mode !== 'annotate') return;
    if (board.frozen) video.pause();
    else void video.play().catch(() => {});
  }, [video, mode, board.frozen]);

  const visible = board.enabled && !board.hidden;
  const drawing = useRef(false);

  if (!visible || !rect) {
    return (
      <div ref={setBox} className="pointer-events-none absolute inset-0">
        {board.enabled && board.hidden && (
          <HideToggle hidden onClick={() => board.setHidden(false)} />
        )}
      </div>
    );
  }

  const aspect = aspectOf(rect);

  const pointFrom = (e: ReactPointerEvent<HTMLCanvasElement>): Point => {
    const bounds = e.currentTarget.getBoundingClientRect();
    return toNormalised(e.clientX - bounds.left, e.clientY - bounds.top, rect);
  };

  const onPointerDown = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!board.canDraw) return;
    // Capture, or a stroke that leaves the canvas never receives its release and hangs open.
    e.currentTarget.setPointerCapture(e.pointerId);
    drawing.current = true;
    board.beginDraw(pointFrom(e), aspect);
  };

  const onPointerMove = (e: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!board.canDraw) return;
    // The laser follows the hand whether or not a button is held; ink does not.
    if (!drawing.current && board.tool !== 'laser') return;
    board.extendDraw(pointFrom(e), aspect);
  };

  const onPointerUp = () => {
    if (!drawing.current) return;
    drawing.current = false;
    board.endDraw();
  };

  return (
    <div ref={setBox} className="pointer-events-none absolute inset-0">
      <BoardCanvas
        rect={rect}
        strokes={board.strokes}
        laser={board.laser}
        interactive={board.canDraw}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        cursor={board.tool === 'eraser' ? 'cell' : board.tool === 'laser' ? 'none' : 'crosshair'}
      />

      {board.pendingText && (
        <TextEntry
          at={toPixels(board.pendingText, rect)}
          offset={{ left: rect.left, top: rect.top }}
          color={board.color}
          size={Math.max(10, board.width * TEXT_SCALE * rect.height)}
          onCommit={board.commitText}
        />
      )}

      {board.canDraw ? (
        <Toolbox
          tool={board.tool}
          color={board.color}
          width={board.width}
          canUndo={board.canUndo}
          frozen={board.frozen}
          canFreeze={mode === 'annotate'}
          onTool={board.setTool}
          onColor={board.setColor}
          onWidth={board.setWidth}
          onUndo={board.undo}
          onClear={board.clear}
          onFreeze={board.setFrozen}
          onClose={board.toggleEnabled}
        />
      ) : (
        <HideToggle hidden={false} onClick={() => board.setHidden(true)} />
      )}
    </div>
  );
};

/**
 * A text stroke is typed in place rather than in a dialog, so the teacher can see where it lands
 * before committing. Enter or clicking away commits; Escape abandons it.
 */
const TextEntry = ({
  at,
  offset,
  color,
  size,
  onCommit,
}: {
  at: { x: number; y: number };
  offset: { left: number; top: number };
  color: string;
  size: number;
  onCommit: (text: string) => void;
}) => {
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => ref.current?.focus(), []);

  return (
    <input
      ref={ref}
      aria-label="Text to place on the board"
      className="pointer-events-auto absolute border-b-2 bg-transparent outline-none"
      style={{
        left: offset.left + at.x,
        top: offset.top + at.y - size,
        color,
        borderColor: color,
        fontSize: size,
        fontWeight: 600,
        minWidth: '6rem',
      }}
      onBlur={(e) => onCommit(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') onCommit(e.currentTarget.value);
        if (e.key === 'Escape') onCommit('');
      }}
    />
  );
};

const HideToggle = ({ hidden, onClick }: { hidden: boolean; onClick: () => void }) => (
  <button
    type="button"
    onClick={onClick}
    className="pointer-events-auto absolute bottom-3 right-3 flex items-center gap-1.5 rounded-lg border border-white/10 bg-slate-900/80 px-2.5 py-1.5 text-xs font-medium text-slate-200 backdrop-blur transition-colors hover:bg-slate-800"
  >
    {hidden ? <Eye size={14} /> : <EyeOff size={14} />}
    {hidden ? "Show teacher's notes" : 'Hide notes'}
  </button>
);
