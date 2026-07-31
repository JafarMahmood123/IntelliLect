import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getStreamDetails, joinStream, leaveStream, updatePublishPolicy, updateRecording } from '../api/streaming';
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

/**
 * Teacher-only: start or stop recording. On success the cached stream details are updated so the
 * control stays correct across re-renders and tab switches without waiting for the SignalR echo.
 *
 * Stopping is final; the server answers a restart with 409, so callers should confirm first.
 */
export const useUpdateRecording = (sessionId: string) => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (enabled: boolean) => updateRecording(sessionId, enabled),
    onSuccess: ({ state }) => {
      queryClient.setQueryData<StreamResponse>(streamingKeys.detail(sessionId), (prev) =>
        prev ? { ...prev, recordingState: state } : prev,
      );
    },
  });
};