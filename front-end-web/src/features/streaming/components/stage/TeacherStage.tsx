import {
  useParticipants,
  useTracks,
  isTrackReference,
} from "@livekit/components-react";
import type { TrackReferenceOrPlaceholder } from "@livekit/components-react";
import { Track } from "livekit-client";
import { useMemo } from "react";
import { TileGrid, ScreenShareView, cameraTrackRefFor } from "./stageShared";

/**
 * Teacher view (Google-Meet style): the teacher sees EVERYONE currently connected. Tiles are
 * driven by the participant list — not by tracks — so this is independent of the session's
 * participation config: a ViewOnly student who can't publish still gets a named placeholder
 * tile. Alone in the room, the teacher just sees themselves; each student that joins adds a tile.
 */
export const TeacherStage = () => {
  // Authoritative set of tiles: self + every connected student, whatever their publish perms.
  const participants = useParticipants();

  // Live media (re-renders on publish/unpublish); used to fill tiles that actually have video.
  const tracks = useTracks(
    [
      { source: Track.Source.Camera, withPlaceholder: false },
      { source: Track.Source.ScreenShare, withPlaceholder: false },
    ],
    { onlySubscribed: false },
  );

  const liveCameras = useMemo(() => {
    const byIdentity = new Map<string, TrackReferenceOrPlaceholder>();
    for (const t of tracks) {
      if (t.source === Track.Source.Camera && isTrackReference(t)) {
        byIdentity.set(t.participant.identity, t);
      }
    }
    return byIdentity;
  }, [tracks]);

  const screen = useMemo(
    () =>
      tracks.find(
        (t) => t.source === Track.Source.ScreenShare && isTrackReference(t),
      ),
    [tracks],
  );

  const tiles = useMemo(
    () => participants.map((p) => cameraTrackRefFor(p, liveCameras)),
    [participants, liveCameras],
  );

  return screen ? (
    <ScreenShareView screen={screen} cameraTiles={tiles} />
  ) : (
    <TileGrid tiles={tiles} />
  );
};
