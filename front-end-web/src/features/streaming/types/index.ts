export type StreamResponse = {
  id: string;
  sessionId: string;
  status: string;
  participantCount: number;
  startedAtUtc: string | null;
  joinToken: string;
  liveKitHost: string;
  participationMode: number; // Added
};

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
