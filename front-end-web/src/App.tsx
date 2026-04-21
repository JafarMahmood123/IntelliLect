import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { ProtectedRoute } from './routes/ProtectedRoute';
import { PublicRoute } from './routes/PublicRoute';
import { useAuthStore } from './store/useAuthStore';
import { LoginForm } from './features/auth/components/LoginForm';
import { RegisterForm } from './features/auth/components/RegisterForm';
import { ThemeProvider } from './components/ui/ThemeProvider';
import { ThemeToggle } from './components/ui/ThemeToggle';

const PendingApprovalPage = () => (
  <div className="flex h-screen items-center justify-center">
    <div className="text-center p-8 bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow-lg">
      <h1 className="text-2xl font-bold mb-2">Account Pending</h1>
      <p className="text-gray-600 dark:text-gray-400">Your account is currently waiting for administrator approval.</p>
      <button onClick={() => useAuthStore.getState().logout()} className="mt-4 text-purple-600 hover:underline">
        Log out
      </button>
    </div>
  </div>
);

const DashboardPage = () => {
  const { user, logout } = useAuthStore();
  return (
    <div className="p-8 w-full max-w-4xl mx-auto">
      <h1 className="text-3xl font-bold mb-6">Welcome, {user?.firstName}!</h1>
      <div className="bg-white dark:bg-gray-900 border dark:border-gray-800 p-6 rounded-lg shadow-lg max-w-md text-left">
        <p className="mb-2"><strong>Email:</strong> {user?.email}</p>
        <p className="mb-2"><strong>Role:</strong> {user?.roleName}</p>
        <p className="mb-4"><strong>Status:</strong> <span className="text-green-500 font-medium">{user?.status}</span></p>
        <button onClick={logout} className="mt-2 bg-red-500 hover:bg-red-600 text-white px-4 py-2 rounded transition-colors">
          Log out
        </button>
      </div>
    </div>
  );
};

function App() {
  return (
    <ThemeProvider>
      <BrowserRouter>
        {/* Global UI */}
        <ThemeToggle />
        
        {/* We moved the background colors to index.css, so we can clean up this wrapper */}
        <div className="min-h-screen flex flex-col items-center justify-center p-4">
          <Routes>
            <Route element={<PublicRoute />}>
              <Route path="/login" element={<LoginForm />} />
              <Route path="/register" element={<RegisterForm />} />
            </Route>

            <Route path="/pending-approval" element={<PendingApprovalPage />} />

            <Route element={<ProtectedRoute />}>
              <Route path="/" element={<DashboardPage />} />
            </Route>
          </Routes>
        </div>
      </BrowserRouter>
    </ThemeProvider>
  );
}

export default App;