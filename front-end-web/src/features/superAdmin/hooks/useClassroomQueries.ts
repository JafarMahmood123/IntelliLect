import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import {
  searchClassrooms,
  createClassroom,
  updateClassroom,
  changeClassroomTeacher,
  getClassroomDeletionImpact,
  deleteClassroom,
} from '../api/classrooms';
import type {
  ChangeClassroomTeacherRequest,
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

export const useChangeClassroomTeacher = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, data }: { id: string; data: ChangeClassroomTeacherRequest }) =>
      changeClassroomTeacher(id, data),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['classrooms'] });
    },
  });
};

// Lazily fetched only when the delete dialog opens for a specific classroom (enabled by id).
export const useClassroomDeletionImpact = (id: string | null) => {
  return useQuery({
    queryKey: ['classroom-deletion-impact', id],
    queryFn: () => getClassroomDeletionImpact(id as string),
    enabled: !!id,
    staleTime: 0,
    gcTime: 0,
  });
};

export const useDeleteClassroom = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) =>
      deleteClassroom(id, reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['classrooms'] });
    },
  });
};
