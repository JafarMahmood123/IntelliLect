import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getStreamDetails, joinStream, leaveStream, updatePublishPolicy } from '../api/streaming';
import type { PublishPolicy, StreamResponse } from '../types';

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

/** Teacher-only: change the student publish policy. On success the cached stream details are
 *  updated so the toggles stay correct across re-renders / tab switches. */
export const useUpdatePublishPolicy = (sessionId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (policy: PublishPolicy) => updatePublishPolicy(sessionId, policy),
    onSuccess: (policy) => {
      queryClient.setQueryData<StreamResponse>(streamingKeys.detail(sessionId), (prev) =>
        prev
          ? {
              ...prev,
              studentsCanPublishAudio: policy.canPublishAudio,
              studentsCanPublishVideo: policy.canPublishVideo,
            }
          : prev,
      );
    },
  });
};