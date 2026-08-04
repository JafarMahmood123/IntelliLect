/**
 * Frontend media configuration: fallback defaults + validation of the server-supplied values.
 *
 * The SERVER owns these settings (StreamingService's "Media" appsettings section, delivered in the
 * join payload as `StreamResponse.media`). This file exists for two reasons:
 *
 *   1. FALLBACK. If the server omits `media` — older build, or the section is missing — the room
 *      still gets sensible options instead of reverting to livekit-client's raw defaults, which had
 *      adaptiveStream and dynacast OFF and maxRetries at 1.
 *   2. VALIDATION. `videoCodec` and `audioPreset` are strings crossing a service boundary into a
 *      third-party library. A server-side typo must degrade to a default, never reach the SDK.
 *
 * Values here are kept in sync with `MediaOptions.cs`, which is the authority and carries the full
 * reasoning for each one. Do not "fix" a value here without reading that file — three of these
 * settings (dtx, stopMicTrackOnMute, simulcast) have non-obvious consequences for the AI
 * assistant's audio pipeline.
 */

import type { MediaSettings } from "../types";

/**
 * Video codecs livekit-client accepts. Anything else is rejected in favour of the default: the SDK
 * would otherwise attempt an unsupported codec and the publish fails at negotiation time, which
 * surfaces as "no video" rather than as a config error.
 */
export const ALLOWED_VIDEO_CODECS = ["vp8", "h264", "vp9", "av1", "h265"] as const;

/** Opus bitrate profiles exposed by livekit-client's AudioPresets. */
export const ALLOWED_AUDIO_PRESETS = [
  "telephone",
  "speech",
  "music",
  "musicStereo",
  "musicHighQuality",
  "musicHighQualityStereo",
] as const;

export type VideoCodecName = (typeof ALLOWED_VIDEO_CODECS)[number];
export type AudioPresetName = (typeof ALLOWED_AUDIO_PRESETS)[number];

/**
 * The wire type with every field present and the two enums narrowed.
 *
 * `MediaSettings` has all fields optional (the server may omit any of them), which makes it unusable
 * as a source of fallbacks — every read would still be `T | undefined`. This is the fully-resolved
 * shape, so `MEDIA_FALLBACK.videoWidth` is a `number` and `.videoCodec` is a real codec name rather
 * than an arbitrary string.
 */
export type ResolvedMediaSettings = Omit<
  Required<MediaSettings>,
  "videoCodec" | "audioPreset"
> & {
  videoCodec: VideoCodecName;
  audioPreset: AudioPresetName;
};

/**
 * Used when the server sends no `media` object at all. Mirrors the shipped MediaOptions.cs defaults.
 */
export const MEDIA_FALLBACK: ResolvedMediaSettings = {
  // Both were false in livekit-client's defaults — the whole point of this feature.
  adaptiveStream: true,
  dynacast: true,

  simulcast: true,
  videoCodec: "vp8",
  audioPreset: "music",
  dtx: true,
  red: true,
  stopMicTrackOnMute: false,

  videoWidth: 1920,
  videoHeight: 1080,
  videoFramerate: 30,

  screenShareWidth: 1920,
  screenShareHeight: 1080,
  // 5, not the library's 15: framerate is what costs the publisher CPU, resolution is what keeps
  // slide text readable.
  screenShareFramerate: 5,
  // The share is already 1080p, so this cap — not the resolution — is what gates how legible it
  // looks. A ceiling, not a target: static slides sit far below it.
  screenShareMaxBitrate: 3_000_000,

  // 5, not the library's 1. At 1, a single failed reconnect dropped the participant out entirely.
  maxRetries: 5,
  peerConnectionTimeoutMs: 15_000,
  websocketTimeoutMs: 15_000,
};

export const isVideoCodec = (value: unknown): value is VideoCodecName =>
  typeof value === "string" && (ALLOWED_VIDEO_CODECS as readonly string[]).includes(value);

export const isAudioPreset = (value: unknown): value is AudioPresetName =>
  typeof value === "string" && (ALLOWED_AUDIO_PRESETS as readonly string[]).includes(value);

/**
 * A positive finite integer, or the fallback. Guards against a misconfigured 0 or a negative
 * dimension/timeout reaching the SDK, where it would either throw or silently disable the feature.
 */
export const positiveIntOr = (value: unknown, fallback: number): number =>
  typeof value === "number" && Number.isFinite(value) && value > 0 ? Math.floor(value) : fallback;

export const boolOr = (value: unknown, fallback: boolean): boolean =>
  typeof value === "boolean" ? value : fallback;
