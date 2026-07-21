import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import {
  searchSessions,
  getLiveSessions,
  forceEndSession,
  getSessionDeletionImpact,
  deleteSession,
} from '../api/sessions';
import type { SearchSessionsParams } from '../types';

export const useSessions = (params: SearchSessionsParams) => {
  return useQuery({
    queryKey: ['sessions', params],
    queryFn: () => searchSessions(params),
    placeholderData: keepPreviousData,
    staleTime: 15_000,
  });
};

// Live view: polls so the participant/recording/assistant figures stay current (step 4).
export const useLiveSessions = (enabled: boolean) => {
  return useQuery({
    queryKey: ['sessions', 'live'],
    queryFn: () => getLiveSessions(),
    enabled,
    refetchInterval: enabled ? 10_000 : false,
    staleTime: 0,
  });
};

export const useForceEndSession = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ sessionId, reason }: { sessionId: string; reason: string }) =>
      forceEndSession(sessionId, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['sessions'] });
    },
  });
};

// Lazily fetched only when the delete dialog opens for a specific session (enabled by id).
export const useSessionDeletionImpact = (sessionId: string | null) => {
  return useQuery({
    queryKey: ['session-deletion-impact', sessionId],
    queryFn: () => getSessionDeletionImpact(sessionId as string),
    enabled: !!sessionId,
    staleTime: 0,
    gcTime: 0,
  });
};

export const useDeleteSession = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ sessionId, reason }: { sessionId: string; reason: string }) =>
      deleteSession(sessionId, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['sessions'] });
    },
  });
};
