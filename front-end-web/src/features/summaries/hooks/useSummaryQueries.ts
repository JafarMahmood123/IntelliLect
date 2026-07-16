import { useQuery } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { getSummaries } from '../api/summaries';
import type { Summary } from '../types';

export const summaryKeys = {
  all: ['summaries'] as const,
  list: (classroomId: string, sessionId?: string) =>
    [...summaryKeys.all, classroomId, sessionId ?? 'all'] as const,
};

/** Poll every 8s while any summary is still generating; otherwise stop. */
const POLL_INTERVAL_MS = 8000;

const hasGenerating = (summaries: Summary[] | undefined) =>
  Boolean(summaries?.some((summary) => summary.status === 'Generating'));

/**
 * Lists a classroom's summaries (newest-first sorting is done in the view),
 * optionally filtered by session. A 403 (not a member / not permitted) is
 * treated as an empty list — a friendly empty state, never an error.
 */
export const useClassroomSummaries = (
  classroomId: string,
  sessionId?: string,
) => {
  return useQuery({
    queryKey: summaryKeys.list(classroomId, sessionId),
    queryFn: async () => {
      try {
        return await getSummaries(classroomId, sessionId);
      } catch (error) {
        if (isAxiosError(error) && error.response?.status === 403) {
          return [] as Summary[];
        }
        throw error;
      }
    },
    enabled: Boolean(classroomId),
    // Light polling: keep refreshing only while something is generating.
    refetchInterval: (query) =>
      hasGenerating(query.state.data) ? POLL_INTERVAL_MS : false,
    retry: (failureCount, error) => {
      if (isAxiosError(error) && error.response?.status === 403) {
        return false;
      }
      return failureCount < 2;
    },
  });
};
