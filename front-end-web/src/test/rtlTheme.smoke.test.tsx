import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import i18n from '../lib/i18n';
import { StatusBadge } from '../components/ui/StatusBadge';
import { QaPanel } from '../features/qa/components/QaPanel';
import { renderWithProviders } from './test-utils';

// Smoke tests: render key F-0..F-5 surfaces with the app in Arabic (RTL) and in
// dark theme, asserting they mount, resolve Arabic chrome, and flip document
// direction — i.e. no RTL/theme wiring is missing.

vi.mock('../features/qa/api/qa', () => ({
  askClassroomQuestion: vi.fn().mockResolvedValue({
    answer: '',
    sources: [],
    hasAnswer: false,
  }),
}));

describe('RTL + dark smoke', () => {
  beforeEach(async () => {
    await i18n.changeLanguage('ar');
  });

  afterEach(async () => {
    await i18n.changeLanguage('en');
    document.documentElement.classList.remove('dark');
  });

  it('flips document direction to RTL in Arabic', () => {
    expect(document.documentElement.dir).toBe('rtl');
  });

  it('StatusBadge renders the Arabic label in a dark container', () => {
    renderWithProviders(
      <div className="dark">
        <StatusBadge status="Available" />
      </div>,
    );
    // Arabic label for "Available" (status conveyed by text, not color alone).
    expect(screen.getByText('متاح')).toBeInTheDocument();
  });

  it('QaPanel mounts with Arabic chrome in RTL', async () => {
    renderWithProviders(
      <div className="dark">
        <QaPanel classroomId="class-rtl" />
      </div>,
    );

    // Arabic panel title + input label resolve (no raw keys, mounts cleanly).
    await waitFor(() =>
      expect(screen.getByText('اسأل عن هذا الفصل الدراسي')).toBeInTheDocument(),
    );
    expect(screen.getByLabelText('سؤالك')).toBeInTheDocument();
    expect(document.documentElement.dir).toBe('rtl');
  });
});
