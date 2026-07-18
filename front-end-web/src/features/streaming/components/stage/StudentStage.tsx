import { useTracks, isTrackReference } from "@livekit/components-react";
import { Track } from "livekit-client";
import { useMemo } from "react";
import {
  TileGrid,
  ScreenShareView,
  StageMessage,
  isTeacherParticipant,
} from "./stageShared";

/**
 * Student view: a student only ever sees the teacher — never other students — regardless of
 * what the session's participation mode lets students publish. Track visibility is filtered by
 * the publisher's role metadata, so this holds even if students are allowed to share audio/video.
 */
export const StudentStage = () => {
  const tracks = useTracks(
    [
      { source: Track.Source.Camera, withPlaceholder: true },
      { source: Track.Source.ScreenShare, withPlaceholder: false },
    ],
    { onlySubscribed: false },
  );

  const teacherTracks = useMemo(
    () => tracks.filter((t) => isTeacherParticipant(t.participant.metadata)),
    [tracks],
  );

  const screen = useMemo(
    () =>
      teacherTracks.find(
        (t) => t.source === Track.Source.ScreenShare && isTrackReference(t),
      ),
    [teacherTracks],
  );

  const cameras = useMemo(
    () => teacherTracks.filter((t) => t.source === Track.Source.Camera),
    [teacherTracks],
  );

  if (!screen && cameras.length === 0) {
    return <StageMessage text="Waiting for the teacher to start…" />;
  }

  return screen ? (
    <ScreenShareView screen={screen} cameraTiles={cameras} />
  ) : (
    <TileGrid tiles={cameras} />
  );
};
