export interface AdminQueryResult {
  id: string;
  userName: string;
  email: string;
  firstName: string;
  lastName: string;
  roleName: string;
  status: string;
  bio?: string;
  createdAtUtc: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
}

export interface CreateAdminRequest {
  userName: string;
  email: string;
  firstName: string;
  lastName: string;
  password: string;
  bio?: string;
}