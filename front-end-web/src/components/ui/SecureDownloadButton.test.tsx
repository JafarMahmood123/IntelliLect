import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SecureDownloadButton } from './SecureDownloadButton';
import { renderWithProviders } from '../../test/test-utils';

describe('SecureDownloadButton', () => {
  beforeEach(() => {
    vi.spyOn(window, 'open').mockReturnValue(null);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('does NOT fetch the url on mount (URLs are short-lived)', () => {
    const getUrl = vi.fn().mockResolvedValue({
      url: 'https://s3.example.com/rec.mp4',
      expiresAt: '2026-01-01T00:00:00Z',
    });

    renderWithProviders(<SecureDownloadButton getUrl={getUrl} />);

    expect(getUrl).not.toHaveBeenCalled();
  });

  it('fetches a fresh url on click and opens it', async () => {
    const user = userEvent.setup();
    const url = 'https://s3.example.com/rec.mp4';
    const getUrl = vi.fn().mockResolvedValue({
      url,
      expiresAt: '2026-01-01T00:00:00Z',
    });

    renderWithProviders(<SecureDownloadButton getUrl={getUrl} />);

    await user.click(screen.getByRole('button', { name: 'Download' }));

    await waitFor(() => expect(getUrl).toHaveBeenCalledTimes(1));
    expect(window.open).toHaveBeenCalledWith(
      url,
      '_blank',
      'noopener,noreferrer',
    );
  });

  it('shows an error when the url fetch fails', async () => {
    const user = userEvent.setup();
    const getUrl = vi.fn().mockRejectedValue(new Error('boom'));

    renderWithProviders(<SecureDownloadButton getUrl={getUrl} />);

    await user.click(screen.getByRole('button', { name: 'Download' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(window.open).not.toHaveBeenCalled();
  });
});
