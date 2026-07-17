import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { FileIndexingBadge } from './FileIndexingBadge';
import { isTerminalIndexingStatus } from '../hooks/useClassroomQueries';
import { renderWithProviders } from '../../../test/test-utils';
import type { FileIndexingStatus } from '../types';

vi.mock('../api/classrooms', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/classrooms')>()),
  getFileIndexingStatus: vi.fn(),
}));

import { getFileIndexingStatus } from '../api/classrooms';

const mockGetStatus = vi.mocked(getFileIndexingStatus);

const CLASSROOM_ID = 'class-1';
const FILE_ID = 'file-1';

const renderBadge = () =>
  renderWithProviders(
    <FileIndexingBadge classroomId={CLASSROOM_ID} fileId={FILE_ID} />,
  );

describe('isTerminalIndexingStatus', () => {
  it('treats only Done/Failed as terminal (so polling stops there)', () => {
    expect(isTerminalIndexingStatus('Done')).toBe(true);
    expect(isTerminalIndexingStatus('Failed')).toBe(true);
    expect(isTerminalIndexingStatus('Pending')).toBe(false);
    expect(isTerminalIndexingStatus('Processing')).toBe(false);
    expect(isTerminalIndexingStatus(undefined)).toBe(false);
  });
});

describe('FileIndexingBadge', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  const cases: Array<{ status: FileIndexingStatus; label: string }> = [
    { status: 'Pending', label: 'Indexing…' },
    { status: 'Processing', label: 'Indexing…' },
    { status: 'Done', label: 'Ready' },
    { status: 'Failed', label: 'Indexing failed' },
  ];

  for (const { status, label } of cases) {
    it(`maps ${status} to the "${label}" badge (i18n, not a raw key)`, async () => {
      mockGetStatus.mockResolvedValue({ fileId: FILE_ID, status });
      renderBadge();

      expect(await screen.findByText(label)).toBeInTheDocument();
      expect(screen.queryByText(/indexing\./)).not.toBeInTheDocument();
    });
  }

  it('requests the member-authorized endpoint with classroom + file ids', async () => {
    mockGetStatus.mockResolvedValue({ fileId: FILE_ID, status: 'Done' });
    renderBadge();

    await waitFor(() =>
      expect(mockGetStatus).toHaveBeenCalledWith(CLASSROOM_ID, FILE_ID),
    );
  });
});

describe('FileIndexingBadge polling', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.runOnlyPendingTimers();
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('keeps polling while Processing and stops once Done', async () => {
    mockGetStatus.mockResolvedValue({ fileId: FILE_ID, status: 'Processing' });
    renderBadge();

    // Initial fetch.
    await vi.waitFor(() => expect(mockGetStatus).toHaveBeenCalledTimes(1));

    // A Processing status keeps polling on the 5s interval.
    await vi.advanceTimersByTimeAsync(5000);
    expect(mockGetStatus.mock.calls.length).toBeGreaterThanOrEqual(2);

    // Once terminal, polling stops: no further calls after the next interval.
    mockGetStatus.mockResolvedValue({ fileId: FILE_ID, status: 'Done' });
    await vi.advanceTimersByTimeAsync(5000);
    const callsAtTerminal = mockGetStatus.mock.calls.length;
    await vi.advanceTimersByTimeAsync(15000);
    expect(mockGetStatus.mock.calls.length).toBe(callsAtTerminal);
  });
});
