import { useCallback, useEffect, useState } from 'react';
import { useRoomContext } from '@livekit/components-react';
import { RoomEvent } from 'livekit-client';
import type { TeachingSuggestion } from '../types';
import { parseTeachingSuggestion } from '../utils/teachingSuggestion';

/** Keep the panel calm: only ever show the most recent N suggestions. */
export const DEFAULT_MAX_VISIBLE = 5;

interface UseTeachingSuggestionsResult {
  suggestions: TeachingSuggestion[];
  dismiss: (id: string) => void;
  clear: () => void;
}

/**
 * Subscribes to teaching-assistant suggestions on the EXISTING LiveKit room
 * (no new connection). The listener is attached ONLY when `enabled` is true
 * (i.e. the current user is the teacher) and is removed on unmount / when the
 * room changes. Incoming packets are parsed and gated defensively; the newest
 * suggestion is kept at the front and the list is capped at `maxVisible`.
 */
export const useTeachingSuggestions = (
  enabled: boolean,
  maxVisible: number = DEFAULT_MAX_VISIBLE,
): UseTeachingSuggestionsResult => {
  const room = useRoomContext();
  const [suggestions, setSuggestions] = useState<TeachingSuggestion[]>([]);

  useEffect(() => {
    // Hard rule: never attach the listener for non-teachers.
    if (!enabled || !room) return;

    const decoder = new TextDecoder();

    const handleData = (payload: Uint8Array) => {
      const suggestion = parseTeachingSuggestion(payload, decoder);
      if (!suggestion) return; // malformed / wrong type / unknown version
      setSuggestions((previous) =>
        [suggestion, ...previous].slice(0, maxVisible),
      );
    };

    room.on(RoomEvent.DataReceived, handleData);
    return () => {
      room.off(RoomEvent.DataReceived, handleData);
    };
  }, [enabled, room, maxVisible]);

  const dismiss = useCallback((id: string) => {
    setSuggestions((previous) => previous.filter((item) => item.id !== id));
  }, []);

  const clear = useCallback(() => setSuggestions([]), []);

  return { suggestions, dismiss, clear };
};
