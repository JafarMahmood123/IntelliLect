import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { ThemeProvider } from './components/ui/ThemeProvider';
import { AppControls } from './components/ui/AppControls';
import { SuperAdminDashboard } from './features/superAdmin/components/SuperAdminDashboard';
import { LoginForm } from './features/auth/components/LoginForm';
import { RegisterForm } from './features/auth/components/RegisterForm';
import { AuthLayout } from './layouts/AuthLayout';
import { DashboardPage } from './pages/DashboardPage';
import { PendingApprovalPage } from './pages/PendingApprovalPage';
import { ProtectedRoute } from './routes/ProtectedRoute';
import { PublicRoute } from './routes/PublicRoute';
import { RoleProtectedRoute } from './routes/RoleProtectedRoute';

function App() {
  return (
    <ThemeProvider>
      <BrowserRouter>
        <div className="min-h-screen w-full bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
          <AppControls />

          <Routes>
            <Route element={<AuthLayout />}>
              <Route element={<PublicRoute />}>
                <Route path="/login" element={<LoginForm />} />
                <Route path="/register" element={<RegisterForm />} />
              </Route>
            </Route>

            <Route path="/pending-approval" element={<PendingApprovalPage />} />

            <Route element={<ProtectedRoute />}>
              <Route path="/" element={<DashboardPage />} />

              <Route
                element={<RoleProtectedRoute allowedRoles={['SuperAdmin']} />}
              >
                <Route path="/super-admin" element={<SuperAdminDashboard />} />
              </Route>
            </Route>
          </Routes>
        </div>
      </BrowserRouter>
    </ThemeProvider>
  );
}

export default App;