import { createContext, useContext } from 'react';
import type { Point, Stroke, ToolKind } from './types';

/**
 * The whiteboard's public surface, and the context carrying it.
 *
 * Split from the provider so that file exports a component and nothing else — Fast Refresh gives
 * up on a module that mixes the two, and losing hot reload on the component you are actively
 * styling is a bad trade for one fewer file.
 */
export interface WhiteboardApi {
  /** The teacher has the whiteboard open. Students follow this; they do not control it. */
  enabled: boolean;
  /** The shared screen is paused so a still can be annotated. */
  frozen: boolean;
  /** A student chose to hide the layer for themselves. Never leaves this browser. */
  hidden: boolean;
  canDraw: boolean;
  /** Everything to paint, including the shape currently being dragged. */
  strokes: Stroke[];
  laser: Point | null;
  tool: ToolKind;
  color: string;
  width: number;
  canUndo: boolean;
  /** Where the teacher clicked with the text tool, while they are still typing. */
  pendingText: Point | null;

  setTool: (tool: ToolKind) => void;
  setColor: (color: string) => void;
  setWidth: (width: number) => void;
  setHidden: (hidden: boolean) => void;

  toggleEnabled: () => void;
  setFrozen: (frozen: boolean) => void;
  clear: () => void;
  undo: () => void;

  beginDraw: (p: Point, aspect: number) => void;
  extendDraw: (p: Point, aspect: number) => void;
  endDraw: () => void;
  commitText: (text: string) => void;
}

export const WhiteboardContext = createContext<WhiteboardApi | null>(null);

export const useWhiteboard = (): WhiteboardApi => {
  const value = useContext(WhiteboardContext);
  if (!value) throw new Error('useWhiteboard must be used inside a <WhiteboardProvider>.');
  return value;
};
