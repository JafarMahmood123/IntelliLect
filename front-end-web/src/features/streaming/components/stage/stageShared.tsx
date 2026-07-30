import {
  VideoTrack,
  ConnectionQualityIndicator,
  useIsSpeaking,
  useIsMuted,
  isTrackReference,
} from "@livekit/components-react";
import type { TrackReferenceOrPlaceholder } from "@livekit/components-react";
import { Track } from "livekit-client";
import type { Participant } from "livekit-client";
import { Mic, MicOff } from "lucide-react";

/**
 * Shared building blocks for the live-session "stages". The base room shell (LiveRoomPage) owns
 * the LiveKit connection, header, controls and audio; each stage (Teacher/Student) only decides
 * WHICH tiles to render. Tiles are custom-rendered (not LiveKit's <ParticipantTile>/<GridLayout>)
 * so the look is fully under our control and we avoid the library's pagination "updatePages" crash.
 */

/** Reads the role we embed in each participant's LiveKit metadata ({"role":"Teacher"}). */
export const isTeacherParticipant = (metadata?: string): boolean => {
  if (!metadata) return false;
  try {
    return String(JSON.parse(metadata)?.role ?? "").toLowerCase() === "teacher";
  } catch {
    return false;
  }
};

/** Stable React key for a tile — placeholders have no trackSid, so fall back to "ph". */
export const tileKey = (t: TrackReferenceOrPlaceholder): string =>
  `${t.participant.identity}:${t.source}:${isTrackReference(t) ? t.publication.trackSid : "ph"}`;

/**
 * A camera track-ref for a participant: their live camera publication when present, otherwise a
 * placeholder. The placeholder renders the participant's avatar + name, which is how the teacher
 * sees ViewOnly students (who never publish a camera) as named tiles.
 */
export const cameraTrackRefFor = (
  p: Participant,
  liveCamerasByIdentity: Map<string, TrackReferenceOrPlaceholder>,
): TrackReferenceOrPlaceholder =>
  liveCamerasByIdentity.get(p.identity) ?? { participant: p, source: Track.Source.Camera };

/** Up-to-two-letter initials for the avatar fallback. */
const initials = (name: string): string => {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
};

/** A single participant tile: live video when available, else a gradient avatar with initials. */
export const StageTile = ({ trackRef }: { trackRef: TrackReferenceOrPlaceholder }) => {
  const p = trackRef.participant;
  const name = p.name || p.identity;
  const isSpeaking = useIsSpeaking(p);
  const micMuted = useIsMuted({ participant: p, source: Track.Source.Microphone });
  const hasVideo =
    isTrackReference(trackRef) && !!trackRef.publication?.track && !trackRef.publication.isMuted;

  return (
    <div
      className={`group relative h-full w-full overflow-hidden rounded-xl bg-gradient-to-br from-slate-800 to-slate-900 shadow-lg transition-shadow ${
        isSpeaking ? "ring-2 ring-emerald-400/80" : "ring-1 ring-white/10"
      }`}
    >
      {hasVideo ? (
        <VideoTrack trackRef={trackRef} className="h-full w-full object-cover" />
      ) : (
        <div className="flex h-full w-full items-center justify-center">
          <div className="flex aspect-square w-[22%] min-w-[44px] max-w-[104px] items-center justify-center rounded-full bg-gradient-to-br from-violet-500 to-indigo-600 font-semibold text-white shadow-inner">
            <span className="text-[clamp(0.9rem,3vw,1.7rem)]">{initials(name)}</span>
          </div>
        </div>
      )}

      {/* Per-participant connection quality (Excellent/Good/Poor/Lost). Without this, "the video
          looks bad" cannot be attributed between the publish settings, host CPU and the network —
          which is what makes tuning the media options measurable rather than guesswork. */}
      <div className="absolute top-2 right-2 rounded-md bg-black/50 p-1 text-white backdrop-blur-sm">
        <ConnectionQualityIndicator participant={p} />
      </div>

      {/* Bottom scrim + name/mic badge */}
      <div className="pointer-events-none absolute inset-x-0 bottom-0 h-14 bg-gradient-to-t from-black/60 to-transparent" />
      <div className="absolute bottom-2 left-2 flex items-center gap-1.5 rounded-md bg-black/50 px-2 py-1 text-white backdrop-blur-sm">
        {micMuted ? (
          <MicOff size={13} className="shrink-0 text-rose-400" />
        ) : (
          <Mic size={13} className="shrink-0 text-emerald-400" />
        )}
        <span className="max-w-[10rem] truncate text-xs font-medium">{name}</span>
      </div>
    </div>
  );
};

/**
 * Count-aware grid that fills the available area (Google-Meet style): one participant fills the
 * stage, two split it, and so on. Columns ≈ ceil(sqrt(n)), capped at 4. minmax(0,1fr) keeps tiles
 * from overflowing their cells.
 */
export const TileGrid = ({ tiles }: { tiles: TrackReferenceOrPlaceholder[] }) => {
  const cols = Math.min(4, Math.ceil(Math.sqrt(Math.max(tiles.length, 1))));
  return (
    <div
      className="grid h-full w-full gap-3 p-4"
      style={{
        gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`,
        gridAutoRows: "minmax(0, 1fr)",
      }}
    >
      {tiles.map((t) => (
        <StageTile key={tileKey(t)} trackRef={t} />
      ))}
    </div>
  );
};

/** Google-Meet screen-share view: the shared screen is focused, camera tiles run in a strip. */
export const ScreenShareView = ({
  screen,
  cameraTiles,
}: {
  screen: TrackReferenceOrPlaceholder;
  cameraTiles: TrackReferenceOrPlaceholder[];
}) => (
  <div className="flex h-full w-full flex-col gap-2 p-3">
    <div className="min-h-0 flex-1 overflow-hidden rounded-xl bg-black ring-1 ring-white/10">
      <VideoTrack trackRef={screen} className="h-full w-full object-contain" />
    </div>
    {cameraTiles.length > 0 && (
      <div className="flex h-28 shrink-0 gap-2 overflow-x-auto pb-1">
        {cameraTiles.map((t) => (
          <div key={tileKey(t)} className="aspect-video h-full shrink-0">
            <StageTile trackRef={t} />
          </div>
        ))}
      </div>
    )}
  </div>
);

export const StageMessage = ({ text }: { text: string }) => (
  <div className="flex h-full items-center justify-center text-slate-500 text-sm">{text}</div>
);
