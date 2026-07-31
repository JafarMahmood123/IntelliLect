/**
 * LiveKit client media settings, owned by the SERVER (StreamingService's "Media" appsettings
 * section) and delivered with the join token. Mirrors `MediaSettingsResponse` / `MediaOptions.cs`,
 * which is the authority and documents why each value is what it is.
 *
 * Fields are optional because a server that predates this section, or one with the section removed,
 * must still produce a usable room — `mediaDefaults.ts` supplies fallbacks and validates the enums
 * before any of it reaches livekit-client.
 */
export type MediaSettings = {
  adaptiveStream?: boolean;
  dynacast?: boolean;
  simulcast?: boolean;
  videoCodec?: string;
  audioPreset?: string;
  dtx?: boolean;
  red?: boolean;
  stopMicTrackOnMute?: boolean;
  videoWidth?: number;
  videoHeight?: number;
  videoFramerate?: number;
  screenShareWidth?: number;
  screenShareHeight?: number;
  screenShareFramerate?: number;
  screenShareMaxBitrate?: number;
  maxRetries?: number;
  peerConnectionTimeoutMs?: number;
  websocketTimeoutMs?: number;
};

export type StreamResponse = {
  id: string;
  sessionId: string;
  status: string;
  participantCount: number;
  startedAtUtc: string | null;
  joinToken: string;
  liveKitHost: string;
  participationMode: number; // Added
  // Current live policy for whether students may publish audio/video. The teacher can change
  // these in real time from the "Session Settings" tab; changes arrive over SignalR.
  studentsCanPublishAudio: boolean;
  studentsCanPublishVideo: boolean;
  // Recording state at the moment of joining, so a late arrival sees the indicator immediately
  // rather than only after the next change is broadcast.
  recordingState: RecordingState;
  // Server-owned media quality + reconnection settings. Optional: see MediaSettings.
  media?: MediaSettings;
};

/** Whether students may publish each media source. Mirrors the backend PublishPolicyChanged event. */
export type PublishPolicy = {
  canPublishAudio: boolean;
  canPublishVideo: boolean;
};

/**
 * Whether the session is being recorded. Mirrors the backend `RecordingState` enum.
 *
 * Three states rather than a boolean because "not recording" is two different situations: `Off`
 * can still be started, `Ended` cannot. Stopping is final — it is what keeps the archived session
 * one continuous video instead of fragments — so the UI must render those differently.
 */
export type RecordingState = 'Off' | 'Recording' | 'Ended';

// --- Teacher live-feedback (F-3) --------------------------------------------
// Real-time AI teaching-assistant suggestions delivered over the existing
// LiveKit data channel, targeted to the teacher's participant identity.

export type FeedbackType = 'discrepancy' | 'gap' | 'unclear';

/** A citation pointing back into the classroom material. */
export interface SuggestionSource {
  citation: number;
  documentId: string;
  page: number | null;
  slide: number | null;
  section: string | null;
}

/**
 * A normalized suggestion ready to render. `id` and `receivedAt` are assigned
 * on the client at receipt (the wire payload has no stable id).
 */
export interface TeachingSuggestion {
  id: string;
  sessionId: string;
  feedbackType: FeedbackType;
  text: string;
  sources: SuggestionSource[];
  createdAt: string;
  receivedAt: number;
}
