/**
 * Maps the server's media settings onto livekit-client's RoomOptions / RoomConnectOptions.
 *
 * Pure functions on purpose: the mapping is the part worth testing, and keeping it free of React and
 * of any live Room makes it unit-testable without a browser or a media server.
 *
 * Every field is read through a validator from `mediaDefaults`, so a missing `media` object, a
 * partially-populated one, or a bad enum string all degrade to a working configuration rather than
 * reaching the SDK. See `mediaDefaults.ts` for why that matters.
 */

import { AudioPresets } from "livekit-client";
import type { RoomConnectOptions, RoomOptions } from "livekit-client";

import type { MediaSettings } from "../types";
import {
  MEDIA_FALLBACK,
  boolOr,
  isAudioPreset,
  isVideoCodec,
  positiveIntOr,
} from "./mediaDefaults";

/**
 * Room CONSTRUCTION options. These are frozen when the room connects, which is safe here because
 * `<LiveKitRoom>` only mounts once the join payload (and therefore this settings object) has
 * arrived — the same reasoning as the existing `initialPublishRef` device request.
 */
export const toRoomOptions = (media: MediaSettings | undefined): RoomOptions => {
  const m: MediaSettings = media ?? MEDIA_FALLBACK;
  const f = MEDIA_FALLBACK;

  const audioPresetName = isAudioPreset(m.audioPreset) ? m.audioPreset : f.audioPreset;
  const videoCodec = isVideoCodec(m.videoCodec) ? m.videoCodec : f.videoCodec;

  return {
    // Matches the received video layer to the rendered element size and pauses off-screen tracks.
    // Requires tracks be attached via an element the SDK can measure — the stage uses <VideoTrack>,
    // which does that internally.
    adaptiveStream: boolOr(m.adaptiveStream, f.adaptiveStream),
    // Lets the server tell publishers to stop encoding simulcast layers nobody subscribes to.
    dynacast: boolOr(m.dynacast, f.dynacast),

    videoCaptureDefaults: {
      resolution: {
        width: positiveIntOr(m.videoWidth, f.videoWidth),
        height: positiveIntOr(m.videoHeight, f.videoHeight),
        frameRate: positiveIntOr(m.videoFramerate, f.videoFramerate),
      },
    },

    publishDefaults: {
      // The layered encoding adaptiveStream selects from; disabling it makes adaptiveStream moot.
      simulcast: boolOr(m.simulcast, f.simulcast),
      videoCodec,
      // AudioPresets is a namespace of {maxBitrate} objects keyed by name; the name is validated
      // above so this index can never be undefined.
      audioPreset: AudioPresets[audioPresetName],
      // dtx / red / stopMicTrackOnMute affect the audio stream the AI assistant transcribes and its
      // pause detection. Passed through from config; see MediaOptions.cs before changing any of them.
      dtx: boolOr(m.dtx, f.dtx),
      red: boolOr(m.red, f.red),
      stopMicTrackOnMute: boolOr(m.stopMicTrackOnMute, f.stopMicTrackOnMute),
      screenShareEncoding: {
        maxBitrate: positiveIntOr(m.screenShareMaxBitrate, f.screenShareMaxBitrate),
        maxFramerate: positiveIntOr(m.screenShareFramerate, f.screenShareFramerate),
      },
    },
  };
};

/**
 * Connect-time options. `maxRetries` is the important one: livekit-client defaults it to 1, so a
 * single failed reconnect attempt used to eject a participant from the lecture.
 */
export const toRoomConnectOptions = (media: MediaSettings | undefined): RoomConnectOptions => {
  const m = media ?? MEDIA_FALLBACK;
  const f = MEDIA_FALLBACK;
  return {
    maxRetries: positiveIntOr(m.maxRetries, f.maxRetries),
    peerConnectionTimeout: positiveIntOr(m.peerConnectionTimeoutMs, f.peerConnectionTimeoutMs),
    websocketTimeout: positiveIntOr(m.websocketTimeoutMs, f.websocketTimeoutMs),
  };
};

/**
 * Screen-share capture geometry. Kept separate from `toRoomOptions` because livekit-client takes it
 * per-call at `setScreenShareEnabled(true, { ... })` rather than as a room default; the bitrate and
 * framerate half already lives in `publishDefaults.screenShareEncoding` above.
 */
export const toScreenShareCaptureOptions = (media: MediaSettings | undefined) => {
  const m = media ?? MEDIA_FALLBACK;
  const f = MEDIA_FALLBACK;
  return {
    resolution: {
      width: positiveIntOr(m.screenShareWidth, f.screenShareWidth),
      height: positiveIntOr(m.screenShareHeight, f.screenShareHeight),
      frameRate: positiveIntOr(m.screenShareFramerate, f.screenShareFramerate),
    },
  };
};
