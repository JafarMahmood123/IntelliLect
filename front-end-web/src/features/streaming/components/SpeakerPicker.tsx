import { MediaDeviceMenu, useMediaDevices } from '@livekit/components-react';
import { Volume2 } from 'lucide-react';

/**
 * Chooses which device the room's audio plays OUT of.
 *
 * LiveKit's `ControlBar` renders device menus only for `audioinput` and `videoinput` — it has no
 * speaker picker at any variation — so without this there is no way to move audio to a headset
 * from inside the app at all. Plugging headphones in mid-session then leaves sound coming out of
 * the laptop, which the microphone re-captures: the echo ends up in the recording, not just in the
 * room.
 *
 * `MediaDeviceMenu` calls `room.switchActiveDevice('audiooutput', id)` under the hood, which is
 * what applies `setSinkId` to the elements `RoomAudioRenderer` plays through. Changing the
 * operating system's default does NOT move audio that is already playing, which is why this has to
 * exist rather than deferring to the OS.
 *
 * Renders nothing when the browser exposes no output devices. Firefox keeps `setSinkId` behind
 * `media.setsinkid.enabled` and enumerates no `audiooutput` devices without it, so the menu there
 * would be permanently empty; Safari and iOS reject the switch outright (LiveKit warns and no-ops).
 * A control that cannot work is worse than no control.
 */
export const SpeakerPicker = () => {
  const outputs = useMediaDevices({ kind: 'audiooutput' });

  if (outputs.length === 0) return null;

  return (
    <div className="lk-button-group">
      <div className="lk-button" aria-hidden>
        <Volume2 size={16} />
      </div>
      <div className="lk-button-group-menu">
        <MediaDeviceMenu kind="audiooutput" aria-label="Select where audio plays" />
      </div>
    </div>
  );
};
