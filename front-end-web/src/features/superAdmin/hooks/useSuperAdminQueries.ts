import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getAdmins, getGroupedAdmins, searchAdmins, createAdmin, toggleAdminStatus } from '../api/superAdmin';
import type { AdminSortField, SortDirection, CreateAdminRequest, PagedResult, AdminQueryResult, GroupedAdminsResponse } from '../types';

export type AdminsQueryData =
  | { mode: 'list'; data: PagedResult<AdminQueryResult> }
  | { mode: 'search'; data: PagedResult<AdminQueryResult> }
  | { mode: 'grouped'; data: GroupedAdminsResponse };

interface UseAdminsParams {
  hasActiveSearch: boolean;
  groupBy: string;
  searchField: string;
  debouncedSearchText: string;
  statusSearch: string;
  sortBy: AdminSortField;
  sortDirection: SortDirection;
  pageSize: number;
}

// 1. Hook for fetching/searching/grouping admins
export const useAdmins = (params: UseAdminsParams) => {
  return useQuery<AdminsQueryData>({
    queryKey: ['admins', params],
    queryFn: async () => {
      if (params.hasActiveSearch) {
        const searchParams = {
          page: 1,
          pageSize: params.pageSize,
          sortBy: params.sortBy,
          sortDirection: params.sortDirection,
          ...(params.searchField === 'userName' && { userName: params.debouncedSearchText }),
          ...(params.searchField === 'email' && { email: params.debouncedSearchText }),
          ...(params.searchField === 'firstName' && { firstName: params.debouncedSearchText }),
          ...(params.searchField === 'lastName' && { lastName: params.debouncedSearchText }),
          ...(params.searchField === 'status' && { status: params.statusSearch }),
        };
        const data = await searchAdmins(searchParams);
        return { mode: 'search', data };
      }

      if (params.groupBy === 'status') {
        const data = await getGroupedAdmins({
          page: 1,
          pageSize: params.pageSize,
          sortBy: params.sortBy,
          sortDirection: params.sortDirection,
        });
        return { mode: 'grouped', data };
      }

      const data = await getAdmins({
        page: 1,
        pageSize: params.pageSize,
        sortBy: params.sortBy,
        sortDirection: params.sortDirection,
      });
      return { mode: 'list', data };
    },
    placeholderData: (previousData) => previousData,
    staleTime: 15_000,
  });
};

// 2. Hook for toggling admin status
export const useToggleAdminStatus = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, status }: { id: string; status: string }) => toggleAdminStatus(id, status),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey:['admins'] });
    },
  });
};

// 3. Hook for creating an admin
export const useCreateAdmin = () => {
  return useMutation({
    mutationFn: (data: CreateAdminRequest) => createAdmin(data),
  });
};