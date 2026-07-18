import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SecureDownloadButton } from './SecureDownloadButton';
import { renderWithProviders } from '../../test/test-utils';

describe('SecureDownloadButton', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('does NOT run the download on mount (only on click)', () => {
    const onDownload = vi.fn().mockResolvedValue(undefined);

    renderWithProviders(<SecureDownloadButton onDownload={onDownload} />);

    expect(onDownload).not.toHaveBeenCalled();
  });

  it('runs the download on click', async () => {
    const user = userEvent.setup();
    const onDownload = vi.fn().mockResolvedValue(undefined);

    renderWithProviders(<SecureDownloadButton onDownload={onDownload} />);

    await user.click(screen.getByRole('button', { name: 'Download' }));

    await waitFor(() => expect(onDownload).toHaveBeenCalledTimes(1));
  });

  it('shows an error when the download fails', async () => {
    const user = userEvent.setup();
    const onDownload = vi.fn().mockRejectedValue(new Error('boom'));

    renderWithProviders(<SecureDownloadButton onDownload={onDownload} />);

    await user.click(screen.getByRole('button', { name: 'Download' }));

    // An error is surfaced (both an inline message and a toast use role=alert).
    await waitFor(() =>
      expect(screen.getAllByRole('alert').length).toBeGreaterThan(0),
    );
  });
});
