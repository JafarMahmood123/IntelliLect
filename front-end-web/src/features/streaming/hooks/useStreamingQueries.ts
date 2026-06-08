import { useMutation, useQuery } from '@tanstack/react-query';
import { getStreamDetails, joinStream, leaveStream } from '../api/streaming';

export const streamingKeys = {
  all: ['streaming'] as const,
  detail: (sessionId: string) => [...streamingKeys.all, 'detail', sessionId] as const,
};

export const useStreamDetails = (sessionId: string) => {
  return useQuery({
    queryKey: streamingKeys.detail(sessionId),
    queryFn: () => getStreamDetails(sessionId),
    enabled: Boolean(sessionId),
  });
};

export const useJoinStream = () => {
  return useMutation({
    mutationFn: (sid: string) => joinStream(sid),
  });
};

export const useLeaveStream = () => {
  return useMutation({
    mutationFn: (sid: string) => leaveStream(sid),
  });
};