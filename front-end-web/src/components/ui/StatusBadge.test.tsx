import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { StatusBadge } from './StatusBadge';

describe('StatusBadge', () => {
  it('renders the i18n label for a recording status (not the raw key)', () => {
    render(<StatusBadge status="Available" />);
    const badge = screen.getByText('Available');
    expect(badge).toBeInTheDocument();
    // The i18n key itself must never leak into the UI.
    expect(screen.queryByText('statuses.available')).not.toBeInTheDocument();
  });

  it('maps numeric session enums to labels', () => {
    render(<StatusBadge status={1} />);
    expect(screen.getByText('Live')).toBeInTheDocument();
  });

  it('applies success color tokens for Available', () => {
    render(<StatusBadge status="Available" />);
    expect(screen.getByText('Available').className).toContain('text-green-700');
  });

  it('applies pending color tokens for Processing', () => {
    render(<StatusBadge status="Processing" />);
    expect(screen.getByText('Processing').className).toContain('text-amber-700');
  });

  it('applies error color tokens for Failed', () => {
    render(<StatusBadge status="Failed" />);
    expect(screen.getByText('Failed').className).toContain('text-red-700');
  });

  it('falls back to the raw string for unknown statuses', () => {
    render(<StatusBadge status="Weird" />);
    expect(screen.getByText('Weird')).toBeInTheDocument();
  });
});
