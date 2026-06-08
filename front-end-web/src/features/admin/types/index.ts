export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
}

export interface GetUsersParams {
  page?: number;
  pageSize?: number;
  roleId?: string; 
}

export type UserStatusPayload = 'Active' | 'Rejected' | 'Pending' | 'Deactivated';