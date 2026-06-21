import { BrowserRouter, Route, Routes, Navigate } from "react-router-dom";
import { ThemeProvider } from "./components/ui/ThemeProvider";
import { ToastProvider } from "./components/ui/ToastProvider";
import { AppControls } from "./components/ui/AppControls";
import { SuperAdminDashboard } from "./features/superAdmin/components/SuperAdminDashboard";
import { LoginForm } from "./features/auth/components/LoginForm";
import { RegisterForm } from "./features/auth/components/RegisterForm";
import { AuthLayout } from "./layouts/AuthLayout";
import { DashboardPage } from "./pages/DashboardPage";
import { PendingApprovalPage } from "./pages/PendingApprovalPage";
import { ProtectedRoute } from "./routes/ProtectedRoute";
import { PublicRoute } from "./routes/PublicRoute";
import { RoleProtectedRoute } from "./routes/RoleProtectedRoute";
import { AdminDashboard } from "./features/admin/components/AdminDashboard";
import { UserProfilePage } from "./features/users/components/UserProfilePage";
import { ForgotPasswordForm } from "./features/auth/components/ForgotPasswordForm";
import { ResetPasswordForm } from "./features/auth/components/ResetPasswordForm";
import {
  TeacherClassroomDashboard,
  ClassroomDetailsPage,
} from "./features/classrooms";
import { StudentClassroomDashboard } from "./features/classrooms/components/StudentClassroomDashboard";
import { ClassroomDiscoveryPage } from "./features/classrooms/components/ClassroomDiscoveryPage";
import { LiveRoomPage } from "./features/streaming/components/LiveRoomPage";
import { useAuthStore } from "./store/useAuthStore";

/**
 * A helper component to switch between Teacher and Student dashboards
 * at the same URL path.
 */
const ClassroomsHome = () => {
  const { user } = useAuthStore();
  if (user?.roleName === "Teacher") return <TeacherClassroomDashboard />;
  if (user?.roleName === "Student") return <StudentClassroomDashboard />;
  return <Navigate to="/" replace />;
};

function App() {
  return (
    <ThemeProvider>
      <ToastProvider>
        <BrowserRouter>
          <div className="min-h-screen w-full bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
            <AppControls />

            <Routes>
              {/* Public Routes */}
              <Route element={<AuthLayout />}>
                <Route element={<PublicRoute />}>
                  <Route path="/login" element={<LoginForm />} />
                  <Route path="/register" element={<RegisterForm />} />
                  <Route path="/forgot-password" element={<ForgotPasswordForm />} />
                  <Route path="/reset-password" element={<ResetPasswordForm />} />
                </Route>
              </Route>

              {/* Status Routes */}
              <Route path="/pending-approval" element={<PendingApprovalPage />} />

              {/* Protected Routes (Authenticated & Approved) */}
              <Route element={<ProtectedRoute />}>
                <Route path="/" element={<DashboardPage />} />
                <Route path="/profile" element={<UserProfilePage />} />

                {/* Common Classroom Routes */}
                <Route element={<RoleProtectedRoute allowedRoles={["Teacher", "Student"]} />}>
                  <Route path="/classrooms" element={<ClassroomsHome />} />
                  <Route path="/classrooms/:id" element={<ClassroomDetailsPage />} />
                  <Route path="/classrooms/:classroomId/live/:sessionId" element={<LiveRoomPage />} />
                </Route>

                {/* Student Only Routes */}
                <Route element={<RoleProtectedRoute allowedRoles={["Student"]} />}>
                  <Route path="/classrooms/discover" element={<ClassroomDiscoveryPage />} />
                </Route>

                {/* Admin Routes */}
                <Route element={<RoleProtectedRoute allowedRoles={["Admin"]} />}>
                  <Route path="/admin" element={<AdminDashboard />} />
                </Route>

                {/* Super Admin Routes */}
                <Route element={<RoleProtectedRoute allowedRoles={["SuperAdmin"]} />}>
                  <Route path="/super-admin" element={<SuperAdminDashboard />} />
                </Route>
              </Route>

              {/* Fallback */}
              <Route path="*" element={<Navigate to="/" replace />} />
            </Routes>
          </div>
        </BrowserRouter>
      </ToastProvider>
    </ThemeProvider>
  );
}

export default App;