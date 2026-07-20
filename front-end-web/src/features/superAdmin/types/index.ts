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

// --- User directory (search users + view details) ---------------------------

export interface UserSummary {
  id: string;
  userName: string;
  email: string;
  firstName: string;
  lastName: string;
  roleName: string;
  status: string;
  bio?: string | null;
  createdAtUtc: string;
  version: string;
}

export type UserRoleValue = '' | 'Student' | 'Teacher' | 'Admin' | 'SuperAdmin';
export type UserStatusValue = '' | 'Active' | 'Pending' | 'Rejected' | 'Deactivated';

export interface SearchUsersParams {
  searchTerm?: string;
  role?: UserRoleValue;
  status?: UserStatusValue;
  createdFrom?: string;
  createdTo?: string;
  page?: number;
  pageSize?: number;
  sortBy?: AdminSortField | 'role';
  sortDirection?: SortDirection;
}

export interface ClassroomSummary {
  id: string;
  name: string;
  description: string;
  teacherId: string;
  createdAtUtc: string;
  fileCount: number;
  studentCount: number;
}

export interface UserDetailResponse {
  user: UserSummary;
  teaching: ClassroomSummary[];
  enrolled: ClassroomSummary[];
  // Alternate path 7ب: memberships could not be loaded from ClassroomService.
  membershipsUnavailable: boolean;
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

export interface AdminStatusGroupResult {
  status: string;
  items: AdminQueryResult[];
}

export interface GroupedAdminsResponse {
  groups: AdminStatusGroupResult[];
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

export type AdminSortField =
  | 'createdat'
  | 'username'
  | 'email'
  | 'firstname'
  | 'lastname'
  | 'status';

export type SortDirection = 'asc' | 'desc';
export type AdminGroupField = 'status';

export interface GetAdminsParams {
  page?: number;
  pageSize?: number;
  sortBy?: AdminSortField;
  sortDirection?: SortDirection;
  groupBy?: AdminGroupField;
}

export interface SearchAdminsParams {
  userName?: string;
  email?: string;
  firstName?: string;
  lastName?: string;
  status?: string;
  page?: number;
  pageSize?: number;
  sortBy?: AdminSortField;
  sortDirection?: SortDirection;
}