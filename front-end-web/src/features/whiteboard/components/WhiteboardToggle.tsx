import { PenLine } from 'lucide-react';
import { useWhiteboard } from '../context';

/**
 * The teacher's way in and out of the whiteboard.
 *
 * One button for both modes on purpose: what it opens depends on whether a screen is being
 * shared, which the teacher can already see. Two buttons would make them choose between an
 * "annotate" and a "whiteboard" that are the same tools on a different backdrop.
 */
export const WhiteboardToggle = () => {
  const board = useWhiteboard();
  if (!board.canDraw) return null;

  return (
    <button
      type="button"
      onClick={board.toggleEnabled}
      aria-pressed={board.enabled}
      title={board.enabled ? 'Close the whiteboard' : 'Open the whiteboard'}
      className={`flex items-center gap-1.5 rounded-lg px-3 py-2 text-xs font-bold transition-colors ${
        board.enabled
          ? 'bg-violet-500 text-white hover:bg-violet-600'
          : 'bg-white/5 text-slate-300 hover:bg-white/10'
      }`}
    >
      <PenLine size={15} />
      Whiteboard
    </button>
  );
};
