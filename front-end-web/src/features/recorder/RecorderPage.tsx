import { useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import {
  LiveKitRoom,
  RoomAudioRenderer,
  VideoTrack,
  isTrackReference,
  useConnectionState,
  useRemoteParticipants,
  useTracks,
} from '@livekit/components-react';
import type { TrackReferenceOrPlaceholder } from '@livekit/components-react';
import { ConnectionState, Track } from 'livekit-client';
import type { RoomOptions } from 'livekit-client';
import {
  StageTile,
  TileGrid,
  cameraTrackRefFor,
  tileKey,
} from '../streaming/components/stage/stageShared';
import { WhiteboardLayer, WhiteboardProvider, useWhiteboard } from '../whiteboard';

/** The path LiveKit's egress worker is pointed at. Must match `Egress:CustomBaseUrl`. */
export const RECORDER_PATH = '/recorder';

/**
 * livekit-client defaults adaptiveStream to FALSE, which for a recorder is the expensive choice:
 * without it this page subscribes to the highest simulcast layer — a 1080p screen share — and then
 * scales it down to the egress viewport (960x540) on every frame, inside the same headless Chrome
 * that was already the bottleneck. Dropped frames were counted at video_input_queue, the capture
 * side, never at the encoder.
 *
 * With it on, each track arrives at the layer nearest the size it is actually drawn at: roughly
 * the viewport for the shared screen, and something tiny for the 112px camera tiles. Same picture,
 * far less decode — which is headroom that can be spent on a higher Egress:Width/Height instead.
 *
 * Note this does NOT raise the recording's resolution. That is capped by the egress viewport and
 * nothing else; adaptive streaming only decides which layer is fetched to fill it.
 *
 * Frozen at module scope on purpose — handing <LiveKitRoom> a fresh object each render churns the
 * room, the same trap as roomOptions in LiveRoomPage.
 */
const RECORDER_ROOM_OPTIONS: RoomOptions = { adaptiveStream: true };

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
        options={RECORDER_ROOM_OPTIONS}
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
      <FocusedCapture tiles={tiles}>
        {(setVideo) => (
          <>
            {isTrackReference(screen) && (
              <VideoTrack
                ref={setVideo}
                trackRef={screen}
                className="h-full w-full object-contain"
              />
            )}
          </>
        )}
      </FocusedCapture>
    );
  }

  return board.enabled ? (
    <FocusedCapture tiles={tiles} board />
  ) : (
    <TileGrid tiles={tiles} />
  );
};

/**
 * The recorded frame: the material edge to edge, cameras as small corner tiles.
 *
 * The live stage puts cameras in a full-width strip under the slide, which is right on a big
 * screen. A recording is not a big screen — this one is 960x540, and a 112px strip is a fifth of
 * its height. Fitting a 16:9 slide into what is left made a 2.39:1 box, so the picture was
 * pillarboxed down to 54% of the frame with 22% of it spent on black bars. Measured on
 * recording-20260801-193640: 724x388 of a 928x388 box.
 *
 * Corner tiles buy that back — a 16:9 share now renders at ~91% of the frame. The trade is that
 * they sit ON the material rather than beside it, which is what every conferencing tool does with
 * a recording, and for the same reason: at this size, legible slides beat undisturbed corners.
 */
const FocusedCapture = ({
  tiles,
  board = false,
  children,
}: {
  tiles: TrackReferenceOrPlaceholder[];
  board?: boolean;
  children?: (setVideo: (el: HTMLVideoElement | null) => void) => ReactNode;
}) => {
  const [video, setVideo] = useState<HTMLVideoElement | null>(null);

  return (
    <div className={`relative h-full w-full ${board ? 'bg-white' : 'bg-black'}`}>
      {children?.(setVideo)}

      <WhiteboardLayer
        mode={board ? 'board' : 'annotate'}
        video={board ? null : video}
        controls={false}
      />

      {/* Capped at three: past that the tiles either shrink to unrecognisable or eat the slide.
          A lecture recording is about the material; the faces are context. */}
      <div className="pointer-events-none absolute bottom-2 end-2 flex gap-1.5">
        {tiles.slice(0, 3).map((t) => (
          <div key={tileKey(t)} className="aspect-video w-28 shrink-0 opacity-95">
            <StageTile trackRef={t} />
          </div>
        ))}
      </div>
    </div>
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
