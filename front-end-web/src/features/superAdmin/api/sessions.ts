import { apiClient } from '../../../lib/axios';
import type {
  ForceEndSessionResult,
  LiveSessionsResponse,
  PagedResult,
  SearchSessionsParams,
  SessionDeletionImpact,
  SessionDeletionSummary,
  SessionMonitorItem,
} from '../types';

export const searchSessions = async (
  params: SearchSessionsParams = {},
): Promise<PagedResult<SessionMonitorItem>> => {
  const response = await apiClient.get<PagedResult<SessionMonitorItem>>(
    '/super-admin/sessions',
    {
      params: {
        search: params.search || undefined,
        status: params.status || undefined,
        classroomId: params.classroomId || undefined,
        page: params.page ?? 1,
        pageSize: params.pageSize ?? 20,
      },
    },
  );
  return response.data;
};

export const getLiveSessions = async (): Promise<LiveSessionsResponse> => {
  const response = await apiClient.get<LiveSessionsResponse>(
    '/super-admin/sessions/live',
  );
  return response.data;
};

export const forceEndSession = async (
  sessionId: string,
  reason: string,
): Promise<ForceEndSessionResult> => {
  const response = await apiClient.post<ForceEndSessionResult>(
    `/super-admin/sessions/${sessionId}/force-end`,
    { reason },
  );
  return response.data;
};

// Step 3: read-only preview of what deleting the session will destroy.
export const getSessionDeletionImpact = async (
  sessionId: string,
): Promise<SessionDeletionImpact> => {
  const response = await apiClient.get<SessionDeletionImpact>(
    `/super-admin/sessions/${sessionId}/deletion-impact`,
  );
  return response.data;
};

// Steps 5-6: delete the session and its outputs. The reason is mandatory (4أ).
export const deleteSession = async (
  sessionId: string,
  reason: string,
): Promise<SessionDeletionSummary> => {
  const response = await apiClient.delete<SessionDeletionSummary>(
    `/super-admin/sessions/${sessionId}`,
    { data: { reason } },
  );
  return response.data;
};
