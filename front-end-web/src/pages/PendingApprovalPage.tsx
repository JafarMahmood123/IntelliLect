import { Button } from '../components/ui/Button';
import { useAuthStore } from '../store/useAuthStore';

export const PendingApprovalPage = () => {
  const logout = useAuthStore((state) => state.logout);

  return (
    <div className="min-h-screen flex items-center justify-center p-4">
      <div className="w-full max-w-md text-center p-8 bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow-lg">
        <h1 className="text-2xl font-bold mb-2 text-slate-900 dark:text-white">
          Account Pending
        </h1>

        <p className="text-slate-600 dark:text-slate-400 mb-6">
          Your account is currently waiting for administrator approval.
        </p>

        <Button type="button" variant="secondary" onClick={logout}>
          Log out
        </Button>
      </div>
    </div>
  );
};