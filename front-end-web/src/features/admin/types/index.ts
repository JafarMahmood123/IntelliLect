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

/** The server-side action names, distinct from the resulting status. */
export type UserStatusAction = 'Accept' | 'Reject' | 'Deactivate' | 'Reactivate';

/**
 * What happened to ONE account in a bulk request. Every requested id gets a row, including
 * the failures — a caller approving 200 registrations needs to know which three did not take.
 */
export interface BulkUserStatusItem {
  userId: string;
  succeeded: boolean;
  status: string | null;
  error: string | null;
}

/**
 * Partial success is the CONTRACT here, not an error case: the request returns 200 even when
 * some accounts failed, so the caller must read `results` rather than assume the batch took.
 */
export interface BulkUserStatusResult {
  requested: number;
  succeeded: number;
  failed: number;
  results: BulkUserStatusItem[];
}