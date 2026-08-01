import { useEffect, useMemo, useRef } from 'react';
import {
  LiveKitRoom,
  RoomAudioRenderer,
  isTrackReference,
  useConnectionState,
  useRemoteParticipants,
  useTracks,
} from '@livekit/components-react';
import type { TrackReferenceOrPlaceholder } from '@livekit/components-react';
import { ConnectionState, Track } from 'livekit-client';
import {
  BoardView,
  ScreenShareView,
  TileGrid,
  cameraTrackRefFor,
} from '../streaming/components/stage/stageShared';
import { WhiteboardLayer, WhiteboardProvider, useWhiteboard } from '../whiteboard';

/** The path LiveKit's egress worker is pointed at. Must match `Egress:CustomBaseUrl`. */
export const RECORDER_PATH = '/recorder';

/**
 * The page that becomes the lesson recording.
 *
 * Room-composite egress does not composite tracks: it opens headless Chrome, loads this page and
 * captures what it renders. That is the whole reason this exists — the teacher's annotations are
 * drawn on a canvas in a browser, so a template that only knows about tracks cannot see them. Here
 * the recorder is simply another participant watching the board, and the ink lands in the MP4
 * because it is on the screen being captured.
 *
 * NOBODY EVER LOOKS AT THIS PAGE. It is loaded by a robot, with a token in the query string and no
 * session of ours, so it carries no app chrome, no theme controls and no authentication — anything
 * it renders is burned into the recording permanently.
 */
export const RecorderPage = () => {
  const { serverUrl, token } = useMemo(() => {
    const params = new URLSearchParams(window.location.search);
    return { serverUrl: params.get('url') ?? '', token: params.get('token') ?? '' };
  }, []);

  // Deliberately never signals START_RECORDING: LiveKit waits for that log before capturing, so a
  // misconfigured template fails the egress loudly instead of recording an hour of blank video.
  if (!serverUrl || !token) {
    return (
      <div className="flex h-screen w-screen items-center justify-center bg-slate-900 p-8 text-center text-sm text-slate-300">
        Recorder template loaded without a LiveKit url and token. Check Egress:CustomBaseUrl.
      </div>
    );
  }

  return (
    <div className="h-screen w-screen overflow-hidden bg-slate-900">
      <LiveKitRoom
        serverUrl={serverUrl}
        token={token}
        connect
        // A recorder subscribes and never publishes; headless Chrome has no camera or microphone
        // to offer anyway, and asking for them would just log permission failures.
        video={false}
        audio={false}
        className="h-full w-full"
      >
        {/* canDraw=false: the recorder receives the board exactly as a student does, including
            asking for it on join, and cannot alter what it is recording. */}
        <WhiteboardProvider canDraw={false}>
          <RecorderStage />
        </WhiteboardProvider>

        <RoomAudioRenderer />
        <RecordingSignal />
      </LiveKitRoom>
    </div>
  );
};

/**
 * The captured view: the shared screen with the whiteboard over it, a blank board when the
 * teacher opens one, otherwise everyone's cameras.
 *
 * Uses REMOTE participants only. The recorder joins hidden, so nobody else sees it — but it can
 * see itself, and `useParticipants` would put a permanently empty tile of the robot into every
 * recording.
 */
const RecorderStage = () => {
  const participants = useRemoteParticipants();
  const board = useWhiteboard();

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
    () => tracks.find((t) => t.source === Track.Source.ScreenShare && isTrackReference(t)),
    [tracks],
  );

  const tiles = useMemo(
    () => participants.map((p) => cameraTrackRefFor(p, liveCameras)),
    [participants, liveCameras],
  );

  if (screen) {
    return (
      <ScreenShareView
        screen={screen}
        cameraTiles={tiles}
        overlay={(video) => <WhiteboardLayer mode="annotate" video={video} controls={false} />}
      />
    );
  }

  return board.enabled ? (
    <BoardView cameraTiles={tiles}>
      <WhiteboardLayer mode="board" controls={false} />
    </BoardView>
  ) : (
    <TileGrid tiles={tiles} />
  );
};

/**
 * The contract with the egress worker, which reads the browser console.
 *
 * START_RECORDING tells it the page is ready and capture may begin; END_RECORDING tells it to
 * finalise. Our own flow stops egress through the API, so END_RECORDING is the belt to that
 * braces — it covers the room ending underneath us.
 */
const RecordingSignal = () => {
  const state = useConnectionState();
  const started = useRef(false);

  useEffect(() => {
    if (state === ConnectionState.Connected && !started.current) {
      started.current = true;
      console.log('START_RECORDING');
      return;
    }
    if (started.current && state === ConnectionState.Disconnected) {
      console.log('END_RECORDING');
    }
  }, [state]);

  return null;
};
