import { apiClient } from '../../../lib/axios';
import type {
  ForceEndSessionResult,
  LiveSessionsResponse,
  PagedResult,
  SearchSessionsParams,
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
