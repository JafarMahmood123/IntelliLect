import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SessionSettingsPanel } from './SessionSettingsPanel';
import { renderWithProviders } from '../../../test/test-utils';
import type { RecordingState, StreamResponse } from '../types';

vi.mock('../api/streaming', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/streaming')>()),
  getStreamDetails: vi.fn(),
  updateRecording: vi.fn(),
  updatePublishPolicy: vi.fn(),
}));

import { getStreamDetails, updateRecording } from '../api/streaming';

const mockGetStreamDetails = vi.mocked(getStreamDetails);
const mockUpdateRecording = vi.mocked(updateRecording);

const SESSION_ID = 'session-1';

const streamDetails = (recordingState: RecordingState): StreamResponse => ({
  id: 'stream-1',
  sessionId: SESSION_ID,
  status: 'Live',
  participantCount: 3,
  startedAtUtc: '2026-07-31T10:00:00Z',
  joinToken: 'token',
  liveKitHost: 'ws://localhost:7880',
  participationMode: 0,
  studentsCanPublishAudio: false,
  studentsCanPublishVideo: false,
  recordingState,
});

const renderPanel = (recordingState: RecordingState) => {
  mockGetStreamDetails.mockResolvedValue(streamDetails(recordingState));
  return renderWithProviders(
    <SessionSettingsPanel sessionId={SESSION_ID} livePolicy={null} liveRecordingState={null} />,
  );
};

beforeEach(() => {
  vi.clearAllMocks();
});

describe('recording control', () => {
  it('starts recording immediately — there is nothing to lose by starting', async () => {
    mockUpdateRecording.mockResolvedValue({ state: 'Recording' });
    renderPanel('Off');

    await userEvent.click(await screen.findByRole('button', { name: /start recording/i }));

    await waitFor(() => expect(mockUpdateRecording).toHaveBeenCalledWith(SESSION_ID, true));
  });

  it('confirms before stopping, because stopping cannot be undone', async () => {
    mockUpdateRecording.mockResolvedValue({ state: 'Ended' });
    renderPanel('Recording');

    await userEvent.click(await screen.findByRole('button', { name: /stop recording/i }));

    // The first click must NOT stop it — it asks first.
    expect(mockUpdateRecording).not.toHaveBeenCalled();
    expect(screen.getByText(/cannot be resumed/i)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /^stop recording$/i }));
    await waitFor(() => expect(mockUpdateRecording).toHaveBeenCalledWith(SESSION_ID, false));
  });

  it('lets the teacher back out of stopping', async () => {
    renderPanel('Recording');

    await userEvent.click(await screen.findByRole('button', { name: /stop recording/i }));
    await userEvent.click(screen.getByRole('button', { name: /keep recording/i }));

    expect(mockUpdateRecording).not.toHaveBeenCalled();
    expect(screen.queryByText(/cannot be resumed/i)).not.toBeInTheDocument();
  });

  it('offers no way to restart a recording that has already been stopped', async () => {
    // The server answers a restart with 409; the UI must not invite the click at all.
    renderPanel('Ended');

    expect(await screen.findByText(/recording finished/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /start recording/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /stop recording/i })).not.toBeInTheDocument();
  });

  it('prefers the live SignalR state over the state fetched at join', async () => {
    // A teacher who started recording from another tab must not be shown a stale "Start" button.
    mockGetStreamDetails.mockResolvedValue(streamDetails('Off'));
    renderWithProviders(
      <SessionSettingsPanel
        sessionId={SESSION_ID}
        livePolicy={null}
        liveRecordingState="Recording"
      />,
    );

    expect(await screen.findByRole('button', { name: /stop recording/i })).toBeInTheDocument();
  });
});
