import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import { searchOutputs, deleteOutput } from '../api/outputs';
import type { SearchOutputsParams } from '../types';

export const useOutputs = (params: SearchOutputsParams) => {
  return useQuery({
    queryKey: ['outputs', params],
    queryFn: () => searchOutputs(params),
    placeholderData: keepPreviousData,
    staleTime: 10_000,
  });
};

export const useDeleteOutput = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ type, outputId, reason }: { type: string; outputId: string; reason: string }) =>
      deleteOutput(type, outputId, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['outputs'] });
    },
  });
};
