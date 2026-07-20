import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import { searchClassrooms, createClassroom, updateClassroom } from '../api/classrooms';
import type {
  CreateClassroomAdminRequest,
  SearchClassroomsParams,
  UpdateClassroomAdminRequest,
} from '../types';

export const useClassrooms = (params: SearchClassroomsParams) => {
  return useQuery({
    queryKey: ['classrooms', params],
    queryFn: () => searchClassrooms(params),
    placeholderData: keepPreviousData,
    staleTime: 15_000,
  });
};

export const useCreateClassroom = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateClassroomAdminRequest) => createClassroom(data),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['classrooms'] });
    },
  });
};

export const useUpdateClassroom = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateClassroomAdminRequest }) =>
      updateClassroom(id, data),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['classrooms'] });
    },
  });
};
