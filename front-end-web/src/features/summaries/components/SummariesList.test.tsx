import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AxiosError } from 'axios';
import { SummariesList } from './SummariesList';
import { renderWithProviders } from '../../../test/test-utils';
import type { Summary } from '../types';

vi.mock('../api/summaries', () => ({
  getSummaries: vi.fn(),
  getSummary: vi.fn(),
  downloadSummary: vi.fn(),
  fetchSummaryMarkdownText: vi.fn(),
}));

import {
  getSummaries,
  downloadSummary,
  fetchSummaryMarkdownText,
} from '../api/summaries';

const mockGetSummaries = vi.mocked(getSummaries);
const mockDownloadSummary = vi.mocked(downloadSummary);
const mockFetchMarkdown = vi.mocked(fetchSummaryMarkdownText);

const CLASSROOM_ID = 'class-1';

const makeSummary = (overrides: Partial<Summary>): Summary => ({
  summaryId: 'sum-1',
  sessionId: 'session-1',
  classroomId: CLASSROOM_ID,
  status: 'Available',
  createdAt: '2026-01-01T10:00:00Z',
  availableAt: '2026-01-01T10:05:00Z',
  ...overrides,
});

describe('SummariesList', () => {
  beforeEach(() => {
    vi.spyOn(window, 'open').mockReturnValue(null);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.clearAllMocks();
  });

  it('renders summaries newest-first with the correct status badge each', async () => {
    mockGetSummaries.mockResolvedValue([
      makeSummary({
        summaryId: 'old',
        createdAt: '2026-01-01T09:00:00Z',
        status: 'Failed',
      }),
      makeSummary({
        summaryId: 'new',
        createdAt: '2026-01-05T09:00:00Z',
        status: 'Available',
      }),
    ]);

    renderWithProviders(<SummariesList classroomId={CLASSROOM_ID} />);

    expect(await screen.findByText('Available')).toBeInTheDocument();
    expect(screen.getByText('Failed')).toBeInTheDocument();

    const badges = screen.getAllByText(/^(Available|Failed)$/);
    expect(badges[0]).toHaveTextContent('Available');
    expect(badges[1]).toHaveTextContent('Failed');
  });

  it('shows PDF + MD downloads for Available, "preparing" for Generating, message for Failed', async () => {
    mockGetSummaries.mockResolvedValue([
      makeSummary({ summaryId: 'a', status: 'Available' }),
      makeSummary({
        summaryId: 'g',
        status: 'Generating',
        createdAt: '2026-01-01T08:00:00Z',
      }),
      makeSummary({
        summaryId: 'f',
        status: 'Failed',
        createdAt: '2026-01-01T07:00:00Z',
      }),
    ]);

    renderWithProviders(<SummariesList classroomId={CLASSROOM_ID} />);

    expect(
      await screen.findByRole('button', { name: /download pdf/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: /download markdown/i }),
    ).toBeInTheDocument();
    expect(screen.getByText('Preparing summary…')).toBeInTheDocument();
    expect(
      screen.getByText('This summary failed to generate.'),
    ).toBeInTheDocument();
  });

  it('download flow: PDF streams format=pdf on click, MD streams format=md, never on mount', async () => {
    const user = userEvent.setup();
    mockGetSummaries.mockResolvedValue([
      makeSummary({ summaryId: 'sum-9', status: 'Available' }),
    ]);
    mockDownloadSummary.mockResolvedValue(undefined);

    renderWithProviders(<SummariesList classroomId={CLASSROOM_ID} />);

    const pdfButton = await screen.findByRole('button', {
      name: /download pdf/i,
    });
    const mdButton = screen.getByRole('button', { name: /download markdown/i });

    // Never triggered on mount.
    expect(mockDownloadSummary).not.toHaveBeenCalled();

    await user.click(pdfButton);
    await waitFor(() =>
      expect(mockDownloadSummary).toHaveBeenCalledWith(
        CLASSROOM_ID,
        'sum-9',
        'pdf',
      ),
    );

    await user.click(mdButton);
    await waitFor(() =>
      expect(mockDownloadSummary).toHaveBeenCalledWith(
        CLASSROOM_ID,
        'sum-9',
        'md',
      ),
    );
  });

  it('preview fetches the MD artifact and renders it read-only and sanitized', async () => {
    const user = userEvent.setup();
    mockGetSummaries.mockResolvedValue([
      makeSummary({ summaryId: 'sum-p', status: 'Available' }),
    ]);
    const markdown =
      '# Session Recap\n\nKey point one.\n\n<img src="x" onerror="alert(1)">';
    mockFetchMarkdown.mockResolvedValue(markdown);

    renderWithProviders(<SummariesList classroomId={CLASSROOM_ID} />);

    const previewButton = await screen.findByRole('button', {
      name: /preview summary/i,
    });
    await user.click(previewButton);

    const dialog = await screen.findByRole('dialog');
    // Markdown rendered as real elements (heading), fetched via the streaming endpoint.
    expect(await within(dialog).findByText('Session Recap')).toBeInTheDocument();
    expect(mockFetchMarkdown).toHaveBeenCalledWith(CLASSROOM_ID, 'sum-p');

    // Sanitized: the injected onerror image must not survive.
    expect(dialog.querySelector('img[onerror]')).toBeNull();
  });

  it('renders a friendly empty state (no error) on a 403', async () => {
    mockGetSummaries.mockRejectedValue(
      new AxiosError('Forbidden', 'ERR_BAD_REQUEST', undefined, undefined, {
        status: 403,
        data: {},
        statusText: 'Forbidden',
        headers: {},
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        config: {} as any,
      }),
    );

    renderWithProviders(<SummariesList classroomId={CLASSROOM_ID} />);

    expect(await screen.findByText('No summaries yet')).toBeInTheDocument();
    expect(
      screen.queryByText('Could not load summaries'),
    ).not.toBeInTheDocument();
  });

  it('renders the empty state for an empty result', async () => {
    mockGetSummaries.mockResolvedValue([]);

    renderWithProviders(<SummariesList classroomId={CLASSROOM_ID} />);

    expect(await screen.findByText('No summaries yet')).toBeInTheDocument();
  });

  it('routes labels through i18n keys (no leaked raw keys)', async () => {
    mockGetSummaries.mockResolvedValue([makeSummary({})]);

    const { container } = renderWithProviders(
      <SummariesList classroomId={CLASSROOM_ID} />,
    );

    await screen.findByText('Available');
    expect(within(container).queryByText(/summaries\./)).toBeNull();
    expect(within(container).queryByText(/statuses\./)).toBeNull();
  });
});
