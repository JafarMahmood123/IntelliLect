/**
 * Decides whether a LiveKit disconnect should send the participant out of the session.
 *
 * WHY THIS EXISTS. `onDisconnected` used to unconditionally navigate back to the classroom, so a
 * network blip ejected a student out of the lecture — indistinguishable from the teacher ending the
 * session. On a flaky or VPN'd connection that is routine, not an edge case.
 *
 * Note WHEN this fires: livekit-client retries internally and only emits Disconnected once it has
 * exhausted `maxRetries` (which the server now sets to 5, up from the library default of 1). So a
 * recoverable classification here means "the SDK already gave up" — the right response is to tell
 * the user and offer to rejoin, not to silently throw away their place in the class.
 */

import { DisconnectReason } from "livekit-client";

export type DisconnectDisposition = "terminal" | "recoverable";

/**
 * Reasons where staying in the room is pointless or actively wrong, so leaving is correct:
 * the user asked to leave, the room is gone, or the server evicted them.
 */
const TERMINAL_REASONS: ReadonlySet<number> = new Set<number>([
  DisconnectReason.CLIENT_INITIATED, // the user clicked Leave
  DisconnectReason.DUPLICATE_IDENTITY, // same identity joined elsewhere; staying would fight it
  DisconnectReason.PARTICIPANT_REMOVED, // evicted server-side
  DisconnectReason.ROOM_DELETED, // session ended — the room was closed behind us
  DisconnectReason.ROOM_CLOSED,
  DisconnectReason.USER_REJECTED,
]);

/**
 * Everything else is treated as recoverable — notably SIGNAL_CLOSE, CONNECTION_TIMEOUT and
 * MEDIA_FAILURE, which are transport failures, and UNKNOWN_REASON/undefined, which carry no
 * information and must not be assumed terminal. Defaulting to recoverable is the safe direction:
 * the cost of being wrong is a rejoin prompt the user dismisses, versus being thrown out of a
 * lecture that was still running.
 */
export const classifyDisconnect = (reason?: DisconnectReason): DisconnectDisposition =>
  reason !== undefined && TERMINAL_REASONS.has(reason) ? "terminal" : "recoverable";

/**
 * A session end announced over SignalR always wins, regardless of the media-layer reason — the
 * server closes the room right behind that broadcast, so any disconnect at that point is terminal
 * even if the transport reported it as a signal failure.
 */
export const shouldExitSession = (
  reason: DisconnectReason | undefined,
  sessionHasEnded: boolean,
): boolean => sessionHasEnded || classifyDisconnect(reason) === "terminal";
