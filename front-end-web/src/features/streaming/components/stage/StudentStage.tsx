import { useTracks, isTrackReference } from "@livekit/components-react";
import { Track } from "livekit-client";
import { useMemo } from "react";
import {
  TileGrid,
  ScreenShareView,
  BoardView,
  StageMessage,
  isTeacherParticipant,
} from "./stageShared";
import { WhiteboardLayer, useWhiteboard } from "../../../whiteboard";

/**
 * Student view: a student only ever sees the teacher — never other students — regardless of
 * what the session's participation mode lets students publish. Track visibility is filtered by
 * the publisher's role metadata, so this holds even if students are allowed to share audio/video.
 */
export const StudentStage = () => {
  const board = useWhiteboard();
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

  // A blank whiteboard is worth showing even with nothing else published — it is the teacher
  // explaining something, which is exactly what the student joined for.
  if (!screen && cameras.length === 0 && !board.enabled) {
    return <StageMessage text="Waiting for the teacher to start…" />;
  }

  if (screen) {
    return (
      <ScreenShareView
        screen={screen}
        cameraTiles={cameras}
        overlay={(video) => <WhiteboardLayer mode="annotate" video={video} />}
      />
    );
  }

  return board.enabled ? (
    <BoardView cameraTiles={cameras}>
      <WhiteboardLayer mode="board" />
    </BoardView>
  ) : (
    <TileGrid tiles={cameras} />
  );
};
