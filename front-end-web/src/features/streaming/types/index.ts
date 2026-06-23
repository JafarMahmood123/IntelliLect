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
