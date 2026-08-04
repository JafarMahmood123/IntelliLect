import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ClassroomFileList } from './ClassroomFileList';
import { renderWithProviders } from '../../../test/test-utils';

vi.mock('../api/classrooms', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/classrooms')>()),
  getClassroomFiles: vi.fn(),
  getUploadLimits: vi.fn(),
  uploadFile: vi.fn(),
  getFileIndexingStatus: vi.fn(),
}));

import { getClassroomFiles, getUploadLimits, uploadFile, getFileIndexingStatus } from '../api/classrooms';

const mockGetFiles = vi.mocked(getClassroomFiles);
const mockGetLimits = vi.mocked(getUploadLimits);
const mockUpload = vi.mocked(uploadFile);
const mockGetStatus = vi.mocked(getFileIndexingStatus);

const CLASSROOM_ID = 'class-1';
const MAX_BYTES = 1024;

const LIMITS = {
  maxFileSizeBytes: MAX_BYTES,
  allowedContentTypes: ['application/pdf', 'text/plain'],
  allowedExtensions: ['pdf', 'txt', 'md'],
};

/** A File of an exact byte length, without allocating anything real. */
const fileOfSize = (name: string, bytes: number, type: string) =>
  new File([new Uint8Array(bytes)], name, { type });

const renderList = () =>
  renderWithProviders(<ClassroomFileList classroomId={CLASSROOM_ID} isTeacher />);

/**
 * Picks a file with `applyAccept: false`, which is what a real user does by switching the OS
 * dialog to "All files". The accept attribute is a convenience, not a guard — these tests are
 * about the guard, so they deliberately go around it.
 */
const pick = async (file: File) => {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement;
  await userEvent.upload(input, file, { applyAccept: false });
};

describe('ClassroomFileList upload limits', () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  const arrange = () => {
    mockGetFiles.mockResolvedValue([]);
    mockGetLimits.mockResolvedValue(LIMITS);
    mockGetStatus.mockResolvedValue({ fileId: 'f', status: 'Done' });
    mockUpload.mockResolvedValue({
      id: 'f',
      fileName: 'lecture.pdf',
      contentType: 'application/pdf',
      sizeBytes: 10,
      s3Key: 'k',
    });
  };

  it('shows the configured limit rather than a hardcoded one', async () => {
    arrange();
    renderList();

    expect(await screen.findByText(/1 KB/)).toBeInTheDocument();
    expect(screen.getByText(/pdf, txt, md/)).toBeInTheDocument();
  });

  it('narrows the OS file picker to the accepted extensions', async () => {
    arrange();
    renderList();
    await screen.findByText(/1 KB/);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input.accept).toBe('.pdf,.txt,.md');
  });

  it('refuses an over-size file WITHOUT starting the upload', async () => {
    arrange();
    renderList();
    await screen.findByText(/1 KB/);

    await pick(fileOfSize('huge.pdf', MAX_BYTES + 1, 'application/pdf'));

    expect(await screen.findByText('File is too large')).toBeInTheDocument();
    // The whole point of a pre-flight check: the bytes never leave the browser.
    expect(mockUpload).not.toHaveBeenCalled();
  });

  it('accepts a file of exactly the maximum', async () => {
    arrange();
    renderList();
    await screen.findByText(/1 KB/);

    await pick(fileOfSize('exact.pdf', MAX_BYTES, 'application/pdf'));

    await waitFor(() => expect(mockUpload).toHaveBeenCalledTimes(1));
  });

  it('refuses a disallowed type without starting the upload', async () => {
    arrange();
    renderList();
    await screen.findByText(/1 KB/);

    await pick(fileOfSize('clip.mp4', 10, 'video/mp4'));

    expect(await screen.findByText('Unsupported file type')).toBeInTheDocument();
    expect(mockUpload).not.toHaveBeenCalled();
  });

  it('accepts an allowed extension carrying a generic content type', async () => {
    // Browsers routinely send an empty or generic type for Markdown.
    arrange();
    renderList();
    await screen.findByText(/1 KB/);

    await pick(fileOfSize('notes.md', 10, 'application/octet-stream'));

    await waitFor(() => expect(mockUpload).toHaveBeenCalledTimes(1));
  });

  it('lets the server decide when the limits could not be fetched', async () => {
    // Degrading to "block everything" would make a limits outage look like a broken upload.
    arrange();
    mockGetLimits.mockRejectedValue(new Error('limits unavailable'));
    renderList();

    await pick(fileOfSize('huge.pdf', MAX_BYTES + 1, 'application/pdf'));

    await waitFor(() => expect(mockUpload).toHaveBeenCalledTimes(1));
  });
});
