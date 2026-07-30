/**
 * The server->client media settings mapping.
 *
 * Two things are worth pinning. The two settings this feature exists to turn on (adaptiveStream,
 * dynacast) and the reconnection budget must actually reach livekit-client — those were the
 * library-default-off values that made a small tile cost a full-resolution stream and let one blip
 * eject a student. And the validation must hold in BOTH directions: a bad value from the server
 * degrades to a default, while a good one is passed through untouched.
 */

import { AudioPresets } from "livekit-client";
import { describe, expect, it } from "vitest";

import type { MediaSettings } from "../types";
import { MEDIA_FALLBACK } from "./mediaDefaults";
import {
  toRoomConnectOptions,
  toRoomOptions,
  toScreenShareCaptureOptions,
} from "./toRoomOptions";

/** A fully-populated server payload, deliberately different from the fallback in every field. */
const serverPayload: MediaSettings = {
  adaptiveStream: false,
  dynacast: false,
  simulcast: false,
  videoCodec: "h264",
  audioPreset: "speech",
  dtx: false,
  red: false,
  stopMicTrackOnMute: true,
  videoWidth: 640,
  videoHeight: 360,
  videoFramerate: 24,
  screenShareWidth: 1280,
  screenShareHeight: 720,
  screenShareFramerate: 3,
  screenShareMaxBitrate: 500_000,
  maxRetries: 9,
  peerConnectionTimeoutMs: 20_000,
  websocketTimeoutMs: 21_000,
};

describe("toRoomOptions", () => {
  it("passes the server's values through", () => {
    const o = toRoomOptions(serverPayload);
    expect(o.adaptiveStream).toBe(false);
    expect(o.dynacast).toBe(false);
    expect(o.publishDefaults?.simulcast).toBe(false);
    expect(o.publishDefaults?.videoCodec).toBe("h264");
    expect(o.publishDefaults?.audioPreset).toEqual(AudioPresets.speech);
    expect(o.publishDefaults?.dtx).toBe(false);
    expect(o.publishDefaults?.red).toBe(false);
    expect(o.publishDefaults?.stopMicTrackOnMute).toBe(true);
    expect(o.videoCaptureDefaults?.resolution).toMatchObject({
      width: 640,
      height: 360,
      frameRate: 24,
    });
    expect(o.publishDefaults?.screenShareEncoding).toMatchObject({
      maxBitrate: 500_000,
      maxFramerate: 3,
    });
  });

  it("turns adaptiveStream and dynacast ON when the server sends nothing", () => {
    // The regression guard: livekit-client defaults both to FALSE, so falling through to the
    // library instead of our fallback would silently undo the whole optimization.
    const o = toRoomOptions(undefined);
    expect(o.adaptiveStream).toBe(true);
    expect(o.dynacast).toBe(true);
  });

  it("fills in defaults for a partially-populated payload", () => {
    const o = toRoomOptions({ adaptiveStream: false });
    expect(o.adaptiveStream).toBe(false); // honoured
    expect(o.dynacast).toBe(MEDIA_FALLBACK.dynacast); // defaulted
    expect(o.publishDefaults?.videoCodec).toBe(MEDIA_FALLBACK.videoCodec);
    expect(o.videoCaptureDefaults?.resolution).toMatchObject({
      width: MEDIA_FALLBACK.videoWidth,
    });
  });

  it.each(["h265x", "vp10", "", "H264", "av2"])(
    "rejects the invalid codec %o and uses the default",
    (codec) => {
      // An unsupported codec fails at SDP negotiation, which surfaces as "no video" rather than as
      // a config error — so it must never reach the SDK.
      const o = toRoomOptions({ ...serverPayload, videoCodec: codec });
      expect(o.publishDefaults?.videoCodec).toBe(MEDIA_FALLBACK.videoCodec);
    },
  );

  it.each(["shouting", "", "Music", "hifi"])(
    "rejects the invalid audio preset %o and uses the default",
    (preset) => {
      const o = toRoomOptions({ ...serverPayload, audioPreset: preset });
      expect(o.publishDefaults?.audioPreset).toEqual(AudioPresets[MEDIA_FALLBACK.audioPreset]);
    },
  );

  it.each([0, -1, NaN, Infinity])(
    "rejects the non-positive dimension %o and uses the default",
    (bad) => {
      const o = toRoomOptions({ ...serverPayload, videoWidth: bad });
      expect(o.videoCaptureDefaults?.resolution?.width).toBe(MEDIA_FALLBACK.videoWidth);
    },
  );

  it("floors a fractional framerate rather than passing a float to the SDK", () => {
    const o = toRoomOptions({ ...serverPayload, videoFramerate: 23.9 });
    expect(o.videoCaptureDefaults?.resolution?.frameRate).toBe(23);
  });
});

describe("toRoomConnectOptions", () => {
  it("passes the server's reconnection budget through", () => {
    const o = toRoomConnectOptions(serverPayload);
    expect(o.maxRetries).toBe(9);
    expect(o.peerConnectionTimeout).toBe(20_000);
    expect(o.websocketTimeout).toBe(21_000);
  });

  it("defaults maxRetries well above the library's 1", () => {
    // At 1, a single failed reconnect attempt dropped the participant out of the lecture.
    const o = toRoomConnectOptions(undefined);
    expect(o.maxRetries).toBe(MEDIA_FALLBACK.maxRetries);
    expect(o.maxRetries).toBeGreaterThan(1);
  });

  it("rejects a zero retry budget", () => {
    const o = toRoomConnectOptions({ ...serverPayload, maxRetries: 0 });
    expect(o.maxRetries).toBe(MEDIA_FALLBACK.maxRetries);
  });
});

describe("toScreenShareCaptureOptions", () => {
  it("keeps resolution high but framerate low by default", () => {
    // Slides need readable text (resolution) far more than motion (framerate), and framerate is
    // what costs the publisher CPU.
    const o = toScreenShareCaptureOptions(undefined);
    expect(o.resolution.width).toBe(1920);
    expect(o.resolution.height).toBe(1080);
    expect(o.resolution.frameRate).toBe(5);
    expect(o.resolution.frameRate).toBeLessThan(15); // the library's screen-share default
  });

  it("passes the server's values through", () => {
    const o = toScreenShareCaptureOptions(serverPayload);
    expect(o.resolution).toMatchObject({ width: 1280, height: 720, frameRate: 3 });
  });
});
