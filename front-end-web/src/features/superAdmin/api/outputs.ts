import { apiClient } from '../../../lib/axios';
import type {
  OutputDeletionSummary,
  OutputListResult,
  SearchOutputsParams,
} from '../types';

export const searchOutputs = async (
  params: SearchOutputsParams = {},
): Promise<OutputListResult> => {
  const response = await apiClient.get<OutputListResult>('/super-admin/outputs', {
    params: {
      search: params.search || undefined,
      type: params.type || undefined,
      status: params.status || undefined,
      classroomId: params.classroomId || undefined,
      page: params.page ?? 1,
      pageSize: params.pageSize ?? 20,
    },
  });
  return response.data;
};

// Steps 4-6: delete a recording or a summary. The type routes to the right endpoint; reason is
// mandatory (4أ).
export const deleteOutput = async (
  type: string,
  outputId: string,
  reason: string,
): Promise<OutputDeletionSummary> => {
  const segment = type === 'Summary' ? 'summaries' : 'recordings';
  const response = await apiClient.delete<OutputDeletionSummary>(
    `/super-admin/outputs/${segment}/${outputId}`,
    { data: { reason } },
  );
  return response.data;
};
