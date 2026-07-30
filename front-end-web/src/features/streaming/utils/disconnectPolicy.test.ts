/**
 * Which disconnects end the session.
 *
 * This is the fix for a student being ejected from a live lecture by a network blip, so the tests
 * are written around that failure: SIGNAL_CLOSE and friends must NOT exit, while a real session end
 * or a user-initiated leave still must. Getting either direction wrong is a user-visible bug —
 * over-exiting throws people out of class, under-exiting strands them in a dead room.
 */

import { DisconnectReason } from "livekit-client";
import { describe, expect, it } from "vitest";

import { classifyDisconnect, shouldExitSession } from "./disconnectPolicy";

describe("classifyDisconnect", () => {
  it.each([
    ["CLIENT_INITIATED (user clicked Leave)", DisconnectReason.CLIENT_INITIATED],
    ["DUPLICATE_IDENTITY", DisconnectReason.DUPLICATE_IDENTITY],
    ["PARTICIPANT_REMOVED (evicted)", DisconnectReason.PARTICIPANT_REMOVED],
    ["ROOM_DELETED (session ended)", DisconnectReason.ROOM_DELETED],
    ["ROOM_CLOSED", DisconnectReason.ROOM_CLOSED],
    ["USER_REJECTED", DisconnectReason.USER_REJECTED],
  ])("treats %s as terminal", (_label, reason) => {
    expect(classifyDisconnect(reason)).toBe("terminal");
  });

  it.each([
    // The important one: this is the transport failure family behind the observed session drops.
    ["SIGNAL_CLOSE", DisconnectReason.SIGNAL_CLOSE],
    ["CONNECTION_TIMEOUT", DisconnectReason.CONNECTION_TIMEOUT],
    ["MEDIA_FAILURE", DisconnectReason.MEDIA_FAILURE],
    ["STATE_MISMATCH", DisconnectReason.STATE_MISMATCH],
    ["JOIN_FAILURE", DisconnectReason.JOIN_FAILURE],
    ["SERVER_SHUTDOWN", DisconnectReason.SERVER_SHUTDOWN],
    ["UNKNOWN_REASON", DisconnectReason.UNKNOWN_REASON],
  ])("treats %s as recoverable", (_label, reason) => {
    expect(classifyDisconnect(reason)).toBe("recoverable");
  });

  it("treats a missing reason as recoverable", () => {
    // No information must not be read as "the session is over" — defaulting to recoverable costs a
    // dismissible prompt, while defaulting the other way throws someone out of a running lecture.
    expect(classifyDisconnect(undefined)).toBe("recoverable");
  });
});

describe("shouldExitSession", () => {
  it("does not exit on a transport failure while the session is still live", () => {
    expect(shouldExitSession(DisconnectReason.SIGNAL_CLOSE, false)).toBe(false);
  });

  it("exits when the server has announced the session end, whatever the media reason", () => {
    // The SignalR "session ended" broadcast wins: the room is closed right behind it, so even a
    // transport-shaped disconnect is terminal at that point.
    expect(shouldExitSession(DisconnectReason.SIGNAL_CLOSE, true)).toBe(true);
    expect(shouldExitSession(DisconnectReason.CONNECTION_TIMEOUT, true)).toBe(true);
    expect(shouldExitSession(undefined, true)).toBe(true);
  });

  it("exits when the user leaves deliberately", () => {
    expect(shouldExitSession(DisconnectReason.CLIENT_INITIATED, false)).toBe(true);
  });

  it("exits when the room was closed server-side even before the broadcast arrives", () => {
    // Ordering is not guaranteed: the media room can close before the SignalR event lands.
    expect(shouldExitSession(DisconnectReason.ROOM_DELETED, false)).toBe(true);
  });
});
