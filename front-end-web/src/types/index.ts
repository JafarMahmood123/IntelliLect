export interface User {
  id: string;
  userName: string;
  email: string;
  firstName: string;
  lastName: string;
  roleName: 'Student' | 'Teacher' | 'Admin' | 'SuperAdmin';
  status: 'Pending' | 'Active' | 'Rejected' | 'Deactivated';
  bio?: string | null;
  createdAtUtc: string;
  version: string;
}