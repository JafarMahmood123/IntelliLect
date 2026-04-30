export interface Classroom {
  id: string;
  name: string;
  description: string;
  teacherId: string;
  createdAtUtc: string;
  fileCount: number;
  studentCount: number;
}

export interface CreateClassroomRequest {
  name: string;
  description: string;
}

export interface UpdateClassroomRequest {
  name: string;
  description: string;
}