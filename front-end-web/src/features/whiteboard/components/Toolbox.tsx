import {
  ArrowUpRight,
  Circle,
  Eraser,
  Highlighter,
  Minus,
  Pause,
  Pen,
  Play,
  Pointer,
  Square,
  Trash2,
  Type,
  Undo2,
  X,
} from 'lucide-react';
import type { ComponentType } from 'react';
import { PALETTE, WIDTHS } from '../constants';
import type { ToolKind } from '../types';

interface Props {
  tool: ToolKind;
  color: string;
  width: number;
  canUndo: boolean;
  frozen: boolean;
  /** Freezing only means something when there is a moving picture underneath. */
  canFreeze: boolean;
  onTool: (tool: ToolKind) => void;
  onColor: (color: string) => void;
  onWidth: (width: number) => void;
  onUndo: () => void;
  onClear: () => void;
  onFreeze: (frozen: boolean) => void;
  onClose: () => void;
}

const TOOLS: { kind: ToolKind; label: string; Icon: ComponentType<{ size?: number }> }[] = [
  { kind: 'pen', label: 'Pen', Icon: Pen },
  { kind: 'highlighter', label: 'Highlighter', Icon: Highlighter },
  { kind: 'arrow', label: 'Arrow', Icon: ArrowUpRight },
  { kind: 'line', label: 'Line', Icon: Minus },
  { kind: 'rect', label: 'Rectangle', Icon: Square },
  { kind: 'ellipse', label: 'Ellipse', Icon: Circle },
  { kind: 'text', label: 'Text', Icon: Type },
  { kind: 'eraser', label: 'Eraser', Icon: Eraser },
  { kind: 'laser', label: 'Laser pointer', Icon: Pointer },
];

/**
 * The teacher's toolbox.
 *
 * Purely presentational — every piece of state arrives as a prop. That is what lets it be tested
 * without a LiveKit room, which the provider necessarily requires.
 */
export const Toolbox = ({
  tool,
  color,
  width,
  canUndo,
  frozen,
  canFreeze,
  onTool,
  onColor,
  onWidth,
  onUndo,
  onClear,
  onFreeze,
  onClose,
}: Props) => (
  <div
    className="pointer-events-auto absolute bottom-3 left-1/2 flex -translate-x-1/2 flex-wrap items-center justify-center gap-1 rounded-2xl border border-white/10 bg-slate-900/90 p-1.5 shadow-2xl backdrop-blur"
    role="toolbar"
    aria-label="Whiteboard tools"
  >
    {TOOLS.map(({ kind, label, Icon }) => (
      <button
        key={kind}
        type="button"
        title={label}
        aria-label={label}
        aria-pressed={tool === kind}
        onClick={() => onTool(kind)}
        className={`rounded-lg p-2 transition-colors ${
          tool === kind ? 'bg-violet-500 text-white' : 'text-slate-300 hover:bg-white/10'
        }`}
      >
        <Icon size={16} />
      </button>
    ))}

    <Divider />

    {PALETTE.map((swatch) => (
      <button
        key={swatch.value}
        type="button"
        title={swatch.name}
        aria-label={swatch.name}
        aria-pressed={color === swatch.value}
        onClick={() => onColor(swatch.value)}
        className={`h-6 w-6 rounded-full border-2 transition-transform ${
          color === swatch.value ? 'scale-110 border-white' : 'border-white/20 hover:scale-105'
        }`}
        style={{ backgroundColor: swatch.value }}
      />
    ))}

    <Divider />

    {WIDTHS.map((option) => (
      <button
        key={option.value}
        type="button"
        title={option.name}
        aria-label={option.name}
        aria-pressed={width === option.value}
        onClick={() => onWidth(option.value)}
        className={`flex h-8 w-8 items-center justify-center rounded-lg transition-colors ${
          width === option.value ? 'bg-violet-500' : 'hover:bg-white/10'
        }`}
      >
        <span
          className="rounded-full bg-white"
          style={{ width: 4 + option.value * 600, height: 4 + option.value * 600 }}
        />
      </button>
    ))}

    <Divider />

    <Action label="Undo" onClick={onUndo} disabled={!canUndo}>
      <Undo2 size={16} />
    </Action>
    <Action label="Clear board" onClick={onClear}>
      <Trash2 size={16} />
    </Action>

    {canFreeze && (
      <Action
        // Named for what it does to the picture, not for the button's own state: annotations are
        // pinned to the frame, so scrolling the slide leaves them pointing at the wrong thing.
        // Freezing is how a teacher marks up something they are about to scroll away from.
        label={frozen ? 'Resume the screen' : 'Freeze the screen to annotate it'}
        onClick={() => onFreeze(!frozen)}
        active={frozen}
      >
        {frozen ? <Play size={16} /> : <Pause size={16} />}
      </Action>
    )}

    <Action label="Close whiteboard" onClick={onClose}>
      <X size={16} />
    </Action>
  </div>
);

const Divider = () => <span className="mx-1 h-6 w-px bg-white/10" />;

const Action = ({
  label,
  onClick,
  disabled = false,
  active = false,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  active?: boolean;
  children: React.ReactNode;
}) => (
  <button
    type="button"
    title={label}
    aria-label={label}
    onClick={onClick}
    disabled={disabled}
    className={`rounded-lg p-2 transition-colors disabled:cursor-not-allowed disabled:opacity-30 ${
      active ? 'bg-amber-500 text-white' : 'text-slate-300 hover:bg-white/10'
    }`}
  >
    {children}
  </button>
);
