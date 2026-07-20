import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import { searchSessions, getLiveSessions, forceEndSession } from '../api/sessions';
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
