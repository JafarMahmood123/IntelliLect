import { ShieldAlert, ShieldCheck } from 'lucide-react';
import { Button } from '../../../components/ui/Button';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import type { AdminQueryResult } from '../types';

interface AdminsTableProps {
  admins: AdminQueryResult[];
  isLoading: boolean;
  isError: boolean;
  isToggling: boolean;
  onToggleStatus: (id: string, status: string) => void;
}

export const AdminsTable = ({
  admins,
  isLoading,
  isError,
  isToggling,
  onToggleStatus,
}: AdminsTableProps) => {
  if (isLoading) {
    return (
      <div className="bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow p-6">
        <p className="text-slate-600 dark:text-slate-400">Loading admins...</p>
      </div>
    );
  }

  if (isError) {
    return (
      <div className="bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow p-6">
        <p className="text-red-500">Failed to load admins.</p>
      </div>
    );
  }

  return (
    <div className="bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow overflow-hidden">
      <div className="overflow-x-auto">
        <table className="min-w-full text-start text-sm">
          <thead className="bg-gray-50 dark:bg-gray-800 border-b dark:border-gray-700">
            <tr>
              <th className="p-4 font-semibold text-slate-700 dark:text-slate-200">
                Name
              </th>
              <th className="p-4 font-semibold text-slate-700 dark:text-slate-200">
                Email
              </th>
              <th className="p-4 font-semibold text-slate-700 dark:text-slate-200">
                Status
              </th>
              <th className="p-4 font-semibold text-slate-700 dark:text-slate-200">
                Joined
              </th>
              <th className="p-4 text-end font-semibold text-slate-700 dark:text-slate-200">
                Actions
              </th>
            </tr>
          </thead>

          <tbody>
            {admins.length === 0 ? (
              <tr>
                <td
                  colSpan={5}
                  className="p-8 text-center text-slate-500 dark:text-slate-400"
                >
                  No admins found.
                </td>
              </tr>
            ) : (
              admins.map((admin) => {
                const isActive = admin.status === 'Active';

                return (
                  <tr
                    key={admin.id}
                    className="border-b dark:border-gray-800 hover:bg-gray-50 dark:hover:bg-gray-800/50"
                  >
                    <td className="p-4 font-medium text-slate-800 dark:text-slate-200">
                      <div>
                        {admin.firstName} {admin.lastName}
                      </div>
                      <div className="text-xs text-slate-500 dark:text-slate-400">
                        @{admin.userName}
                      </div>
                    </td>

                    <td className="p-4 text-slate-600 dark:text-slate-400">
                      {admin.email}
                    </td>

                    <td className="p-4">
                      <StatusBadge status={admin.status} />
                    </td>

                    <td className="p-4 text-slate-600 dark:text-slate-400">
                      {new Date(admin.createdAtUtc).toLocaleDateString()}
                    </td>

                    <td className="p-4 text-end">
                      <Button
                        type="button"
                        variant="ghost"
                        disabled={isToggling}
                        onClick={() => onToggleStatus(admin.id, admin.status)}
                        className="!px-0 !py-0"
                      >
                        {isActive ? (
                          <span className="flex items-center gap-1 text-red-500">
                            <ShieldAlert size={16} />
                            Deactivate
                          </span>
                        ) : (
                          <span className="flex items-center gap-1 text-green-600 dark:text-green-400">
                            <ShieldCheck size={16} />
                            Reactivate
                          </span>
                        )}
                      </Button>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
};