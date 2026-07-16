import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AxiosError } from 'axios';
import { RecordingsList } from './RecordingsList';
import { renderWithProviders } from '../../../test/test-utils';
import type { Recording } from '../types';

vi.mock('../api/recordings', () => ({
  getRecordings: vi.fn(),
  getRecordingDownloadUrl: vi.fn(),
}));

import { getRecordings, getRecordingDownloadUrl } from '../api/recordings';

const mockGetRecordings = vi.mocked(getRecordings);
const mockGetDownloadUrl = vi.mocked(getRecordingDownloadUrl);

const CLASSROOM_ID = 'class-1';

const makeRecording = (overrides: Partial<Recording>): Recording => ({
  recordingId: 'rec-1',
  sessionId: 'session-1',
  classroomId: CLASSROOM_ID,
  status: 'Available',
  durationSeconds: 754,
  sizeBytes: 1.5 * 1024 * 1024,
  contentType: 'video/mp4',
  createdAt: '2026-01-01T10:00:00Z',
  availableAt: '2026-01-01T10:05:00Z',
  ...overrides,
});

describe('RecordingsList', () => {
  beforeEach(() => {
    vi.spyOn(window, 'open').mockReturnValue(null);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.clearAllMocks();
  });

  it('renders recordings newest-first with the correct status badge each', async () => {
    mockGetRecordings.mockResolvedValue([
      makeRecording({
        recordingId: 'old',
        createdAt: '2026-01-01T09:00:00Z',
        status: 'Failed',
      }),
      makeRecording({
        recordingId: 'new',
        createdAt: '2026-01-05T09:00:00Z',
        status: 'Available',
      }),
    ]);

    renderWithProviders(<RecordingsList classroomId={CLASSROOM_ID} />);

    // Both statuses render as translated badges.
    expect(await screen.findByText('Available')).toBeInTheDocument();
    expect(screen.getByText('Failed')).toBeInTheDocument();

    // Newest-first: the 'Available' (Jan 5) row precedes the 'Failed' (Jan 1) one.
    const badges = screen.getAllByText(/Available|Failed/);
    expect(badges[0]).toHaveTextContent('Available');
    expect(badges[1]).toHaveTextContent('Failed');
  });

  it('shows a download button for Available, "preparing" for Processing, message for Failed', async () => {
    mockGetRecordings.mockResolvedValue([
      makeRecording({ recordingId: 'a', status: 'Available' }),
      makeRecording({
        recordingId: 'p',
        status: 'Processing',
        createdAt: '2026-01-01T08:00:00Z',
      }),
      makeRecording({
        recordingId: 'f',
        status: 'Failed',
        createdAt: '2026-01-01T07:00:00Z',
      }),
    ]);

    renderWithProviders(<RecordingsList classroomId={CLASSROOM_ID} />);

    expect(
      await screen.findByRole('button', { name: /download/i }),
    ).toBeInTheDocument();
    expect(screen.getByText('Preparing recording…')).toBeInTheDocument();
    expect(
      screen.getByText('This recording failed to process.'),
    ).toBeInTheDocument();
  });

  it('download flow: click fetches a fresh url on demand and opens it', async () => {
    const user = userEvent.setup();
    mockGetRecordings.mockResolvedValue([
      makeRecording({ recordingId: 'rec-9', status: 'Available' }),
    ]);
    mockGetDownloadUrl.mockResolvedValue({
      url: 'https://s3.example.com/rec-9.mp4',
      expiresAt: '2026-01-01T00:00:00Z',
    });

    renderWithProviders(<RecordingsList classroomId={CLASSROOM_ID} />);

    const downloadButton = await screen.findByRole('button', {
      name: /download recording/i,
    });

    // The url must NOT be fetched until the click.
    expect(mockGetDownloadUrl).not.toHaveBeenCalled();

    await user.click(downloadButton);

    await waitFor(() =>
      expect(mockGetDownloadUrl).toHaveBeenCalledWith(CLASSROOM_ID, 'rec-9'),
    );
    expect(window.open).toHaveBeenCalledWith(
      'https://s3.example.com/rec-9.mp4',
      '_blank',
      'noopener,noreferrer',
    );
  });

  it('renders a friendly empty state (no error) on a 403', async () => {
    mockGetRecordings.mockRejectedValue(
      new AxiosError('Forbidden', 'ERR_BAD_REQUEST', undefined, undefined, {
        status: 403,
        data: {},
        statusText: 'Forbidden',
        headers: {},
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        config: {} as any,
      }),
    );

    renderWithProviders(<RecordingsList classroomId={CLASSROOM_ID} />);

    expect(
      await screen.findByText('No recordings available'),
    ).toBeInTheDocument();
    // The error state must NOT be shown for a permission denial.
    expect(
      screen.queryByText('Could not load recordings'),
    ).not.toBeInTheDocument();
  });

  it('renders the empty state for an empty result', async () => {
    mockGetRecordings.mockResolvedValue([]);

    renderWithProviders(<RecordingsList classroomId={CLASSROOM_ID} />);

    expect(
      await screen.findByText('No recordings available'),
    ).toBeInTheDocument();
  });

  it('routes labels through i18n keys (no leaked raw keys)', async () => {
    mockGetRecordings.mockResolvedValue([makeRecording({})]);

    const { container } = renderWithProviders(
      <RecordingsList classroomId={CLASSROOM_ID} />,
    );

    await screen.findByText('Available');
    // No untranslated i18n keys should appear anywhere in the output.
    expect(within(container).queryByText(/recordings\./)).toBeNull();
    expect(within(container).queryByText(/statuses\./)).toBeNull();
  });
});
