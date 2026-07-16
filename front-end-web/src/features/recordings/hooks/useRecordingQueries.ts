import { useQuery } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { getRecordings } from '../api/recordings';
import type { Recording } from '../types';

export const recordingKeys = {
  all: ['recordings'] as const,
  list: (classroomId: string, sessionId?: string) =>
    [...recordingKeys.all, classroomId, sessionId ?? 'all'] as const,
};

/** Poll every 8s while any recording is still processing; otherwise stop. */
const POLL_INTERVAL_MS = 8000;

const hasProcessing = (recordings: Recording[] | undefined) =>
  Boolean(recordings?.some((recording) => recording.status === 'Processing'));

/**
 * Lists a classroom's recordings (newest-first sorting is done in the view),
 * optionally filtered by session. A 403 (not a member / not permitted) is
 * treated as an empty list — a friendly empty state, never an error.
 */
export const useClassroomRecordings = (
  classroomId: string,
  sessionId?: string,
) => {
  return useQuery({
    queryKey: recordingKeys.list(classroomId, sessionId),
    queryFn: async () => {
      try {
        return await getRecordings(classroomId, sessionId);
      } catch (error) {
        if (isAxiosError(error) && error.response?.status === 403) {
          return [] as Recording[];
        }
        throw error;
      }
    },
    enabled: Boolean(classroomId),
    // Light polling: keep refreshing only while something is processing.
    refetchInterval: (query) =>
      hasProcessing(query.state.data) ? POLL_INTERVAL_MS : false,
    retry: (failureCount, error) => {
      // Never retry an authorization failure — it's a stable "not permitted".
      if (isAxiosError(error) && error.response?.status === 403) {
        return false;
      }
      return failureCount < 2;
    },
  });
};
