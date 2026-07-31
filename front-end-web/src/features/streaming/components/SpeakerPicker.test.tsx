import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { SpeakerPicker } from './SpeakerPicker';

// MediaDeviceMenu needs a live room context, and useMediaDevices calls enumerateDevices — neither
// exists in jsdom. Both are LiveKit's to get right; what is ours is whether we render at all.
vi.mock('@livekit/components-react', () => ({
  useMediaDevices: vi.fn(),
  MediaDeviceMenu: ({ kind }: { kind: string }) => <button data-kind={kind}>devices</button>,
}));

import { useMediaDevices } from '@livekit/components-react';

const mockUseMediaDevices = vi.mocked(useMediaDevices);

const device = (deviceId: string, label: string) =>
  ({ deviceId, label, kind: 'audiooutput', groupId: 'g' }) as MediaDeviceInfo;

beforeEach(() => {
  vi.clearAllMocks();
});

describe('SpeakerPicker', () => {
  it('offers the output devices the browser exposes', () => {
    mockUseMediaDevices.mockReturnValue([
      device('default', 'Default'),
      device('hdmi-1', 'Headphones'),
    ]);

    render(<SpeakerPicker />);

    expect(screen.getByRole('button', { name: /devices/i })).toHaveAttribute(
      'data-kind',
      'audiooutput',
    );
  });

  it('renders nothing when the browser exposes no output devices', () => {
    // Firefox without media.setsinkid.enabled enumerates none, so the menu would be permanently
    // empty. A control that cannot work is worse than no control.
    mockUseMediaDevices.mockReturnValue([]);

    const { container } = render(<SpeakerPicker />);

    expect(container).toBeEmptyDOMElement();
  });

  it('asks only for audio output devices', () => {
    mockUseMediaDevices.mockReturnValue([device('default', 'Default')]);

    render(<SpeakerPicker />);

    expect(mockUseMediaDevices).toHaveBeenCalledWith({ kind: 'audiooutput' });
  });
});
