import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Toolbox } from './Toolbox';
import { DEFAULT_COLOR, DEFAULT_WIDTH } from '../constants';

const setup = (overrides: Partial<Parameters<typeof Toolbox>[0]> = {}) => {
  const props = {
    tool: 'pen' as const,
    color: DEFAULT_COLOR,
    width: DEFAULT_WIDTH,
    canUndo: false,
    frozen: false,
    canFreeze: true,
    onTool: vi.fn(),
    onColor: vi.fn(),
    onWidth: vi.fn(),
    onUndo: vi.fn(),
    onClear: vi.fn(),
    onFreeze: vi.fn(),
    onClose: vi.fn(),
    ...overrides,
  };

  render(<Toolbox {...props} />);
  return props;
};

describe('Toolbox', () => {
  it('offers the whole teaching set', () => {
    setup();

    for (const label of [
      'Pen',
      'Highlighter',
      'Arrow',
      'Line',
      'Rectangle',
      'Ellipse',
      'Text',
      'Eraser',
      'Laser pointer',
    ]) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument();
    }
  });

  it('shows which tool is in hand', () => {
    setup({ tool: 'eraser' });

    expect(screen.getByRole('button', { name: 'Eraser' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Pen' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('reports a tool, colour and width change', async () => {
    const user = userEvent.setup();
    const props = setup();

    await user.click(screen.getByRole('button', { name: 'Highlighter' }));
    await user.click(screen.getByRole('button', { name: 'Blue' }));
    await user.click(screen.getByRole('button', { name: 'Thick' }));

    expect(props.onTool).toHaveBeenCalledWith('highlighter');
    expect(props.onColor).toHaveBeenCalledWith('#3b82f6');
    expect(props.onWidth).toHaveBeenCalledWith(0.012);
  });

  it('disables undo until there is something to undo', async () => {
    const user = userEvent.setup();
    const props = setup({ canUndo: false });

    const undo = screen.getByRole('button', { name: 'Undo' });
    expect(undo).toBeDisabled();

    await user.click(undo);
    expect(props.onUndo).not.toHaveBeenCalled();
  });

  it('hides freeze on a blank board, where there is no moving picture to freeze', () => {
    setup({ canFreeze: false });

    expect(screen.queryByRole('button', { name: /freeze/i })).not.toBeInTheDocument();
    // The rest of the box is unaffected.
    expect(screen.getByRole('button', { name: 'Pen' })).toBeInTheDocument();
  });

  it('offers to resume once the screen is frozen', async () => {
    const user = userEvent.setup();
    const props = setup({ frozen: true });

    await user.click(screen.getByRole('button', { name: 'Resume the screen' }));

    expect(props.onFreeze).toHaveBeenCalledWith(false);
  });
});
