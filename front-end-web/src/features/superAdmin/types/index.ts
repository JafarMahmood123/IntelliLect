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

export type UserStatusAction = 'Accept' | 'Reject' | 'Deactivate' | 'Reactivate';

// The status transitions a super admin may apply from a given current status.
export const getStatusActions = (status: string): UserStatusAction[] => {
  switch (status) {
    case 'Pending':
      return ['Accept', 'Reject'];
    case 'Active':
      return ['Deactivate'];
    case 'Deactivated':
      return ['Reactivate'];
    default: // Rejected is terminal
      return [];
  }
};

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

// --- Classroom administration -----------------------------------------------

export interface ClassroomAdminItem {
  id: string;
  name: string;
  description: string;
  teacherId: string;
  teacherName?: string | null;
  teacherEmail?: string | null;
  createdAtUtc: string;
  fileCount: number;
  studentCount: number;
  sessionCount: number;
  version: number;
}

export interface SearchClassroomsParams {
  search?: string;
  teacherId?: string;
  page?: number;
  pageSize?: number;
}

export interface CreateClassroomAdminRequest {
  teacherId: string;
  name: string;
  description: string;
}

export interface UpdateClassroomAdminRequest {
  name: string;
  description: string;
  version: number;
}

// --- Session monitoring / force-end -----------------------------------------

export type SessionStatusValue = '' | 'Scheduled' | 'Live' | 'Ended';

export interface SessionMonitorItem {
  sessionId: string;
  classroomId: string;
  className: string;
  teacherId: string;
  teacherName?: string | null;
  teacherEmail?: string | null;
  title: string;
  status: string;
  scheduledAtUtc: string;
  startedAtUtc?: string | null;
  endedAtUtc?: string | null;
  recordingStatus?: string | null;
  summaryStatus?: string | null;
}

export interface SearchSessionsParams {
  search?: string;
  status?: SessionStatusValue;
  classroomId?: string;
  page?: number;
  pageSize?: number;
}

export interface LiveSessionItem {
  sessionId: string;
  classroomId: string;
  className: string;
  teacherId: string;
  teacherName?: string | null;
  title: string;
  startedAtUtc?: string | null;
  // Null when the real-time snapshot could not be fetched (alt 4أ).
  participantCount?: number | null;
  isRecording?: boolean | null;
  assistantRunning?: boolean | null;
}

export interface LiveSessionsResponse {
  items: LiveSessionItem[];
  realtimeUnavailable: boolean;
}

export interface ForceEndSessionResult {
  sessionId: string;
  status: string;
  alreadyEnded: boolean;
  streamEnded: boolean;
  summaryTriggered: boolean;
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