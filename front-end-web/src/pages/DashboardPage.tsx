import { Link } from 'react-router-dom';
import { Button } from '../components/ui/Button';
import { PageHeader } from '../components/ui/PageHeader';
import { StatusBadge } from '../components/ui/StatusBadge';
import { useAuthStore } from '../store/useAuthStore';

export const DashboardPage = () => {
  const { user, logout } = useAuthStore();

  if (!user) {
    return null;
  }

  return (
    <div className="w-full max-w-4xl mx-auto p-6">
      <PageHeader
        title={`Welcome, ${user.firstName}!`}
        description="This is your main dashboard."
      />

      <div className="bg-white dark:bg-gray-900 border dark:border-gray-800 p-6 rounded-lg shadow-lg max-w-2xl">
        <div className="space-y-4 text-slate-700 dark:text-slate-300">
          <p>
            <strong>Email:</strong> {user.email}
          </p>

          <p>
            <strong>Role:</strong> {user.roleName}
          </p>

          <div className="flex items-center gap-2">
            <strong>Status:</strong>
            <StatusBadge status={user.status} />
          </div>
        </div>

        <div className="flex flex-wrap gap-4 mt-6">
          <Button type="button" variant="danger" onClick={logout}>
            Log out
          </Button>

          {user.roleName === 'SuperAdmin' && (
            <Link
              to="/super-admin"
              className="inline-flex items-center justify-center rounded-lg px-4 py-2.5 text-sm font-semibold text-white bg-gradient-to-r from-violet-600 to-indigo-600 shadow-md hover:from-violet-700 hover:to-indigo-700 hover:shadow-lg transition-all"
            >
              Open Super Admin Dashboard
            </Link>
          )}
        </div>
      </div>
    </div>
  );
};