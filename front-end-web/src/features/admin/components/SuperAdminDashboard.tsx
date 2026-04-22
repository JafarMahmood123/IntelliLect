import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getAdmins, toggleAdminStatus } from '../api/superAdmin';
import { ShieldAlert, ShieldCheck } from 'lucide-react';
import { CreateAdminDrawer } from './CreateAdminDrawer';

const getStatusBadgeClasses = (status: string) => {
  switch (status) {
    case 'Active':
      return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
    case 'Pending':
      return 'bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400';
    case 'Rejected':
    case 'Deactivated':
      return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
    default:
      return 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300';
  }
};

const getActionButtonClasses = (isActive: boolean) => {
  if (isActive) {
    return `
      inline-flex items-center justify-center gap-2 w-36 rounded-md px-3 py-2 text-sm font-medium
      border border-red-200 bg-red-50 text-red-600
      hover:bg-red-100 hover:border-red-300
      dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400 dark:hover:bg-red-950/50
      transition-colors cursor-pointer disabled:cursor-not-allowed disabled:opacity-50
    `;
  }

  return `
    inline-flex items-center justify-center gap-2 w-36 rounded-md px-3 py-2 text-sm font-medium
    border border-green-200 bg-green-50 text-green-600
    hover:bg-green-100 hover:border-green-300
    dark:border-green-900/50 dark:bg-green-950/30 dark:text-green-400 dark:hover:bg-green-950/50
    transition-colors cursor-pointer disabled:cursor-not-allowed disabled:opacity-50
  `;
};

export const SuperAdminDashboard = () => {
  const queryClient = useQueryClient();
  const [isCreateDrawerOpen, setIsCreateDrawerOpen] = useState(false);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const { data, isLoading, isError } = useQuery({
    queryKey: ['admins'],
    queryFn: () => getAdmins(1, 50),
  });

  const toggleStatusMutation = useMutation({
    mutationFn: ({ id, status }: { id: string; status: string }) =>
      toggleAdminStatus(id, status),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admins'] });
    },
  });

  const handleAdminCreated = async (adminName: string) => {
    setIsCreateDrawerOpen(false);
    setSuccessMessage(`Admin account for ${adminName} was created successfully.`);
    await queryClient.invalidateQueries({ queryKey: ['admins'] });
  };

  if (isLoading) {
    return <div className="p-8">Loading admins...</div>;
  }

  if (isError) {
    return <div className="p-8 text-red-500">Failed to load admins.</div>;
  }

  return (
    <>
      <div className="w-full max-w-6xl mx-auto p-6">
        <div className="flex justify-between items-center mb-6 gap-4">
          <h1 className="text-3xl font-bold dark:text-white">
            Super Admin Dashboard
          </h1>

          <button
            type="button"
            onClick={() => {
              setSuccessMessage(null);
              setIsCreateDrawerOpen(true);
            }}
            className="bg-purple-600 hover:bg-purple-700 text-white px-4 py-2 rounded-md transition-colors cursor-pointer"
          >
            + Create New Admin
          </button>
        </div>

        {successMessage && (
          <div className="mb-5 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-700 dark:border-green-900/50 dark:bg-green-950/30 dark:text-green-300">
            {successMessage}
          </div>
        )}

        <div className="bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow overflow-hidden">
          <table className="w-full table-fixed text-left text-sm">
            <colgroup>
              <col style={{ width: '24%' }} />
              <col style={{ width: '24%' }} />
              <col style={{ width: '14%' }} />
              <col style={{ width: '18%' }} />
              <col style={{ width: '20%' }} />
            </colgroup>

            <thead className="bg-gray-50 dark:bg-gray-800 border-b dark:border-gray-700">
              <tr>
                <th className="p-4 text-center">Name</th>
                <th className="p-4 text-center">Email</th>
                <th className="p-4 text-center">Status</th>
                <th className="p-4 text-center">Joined</th>
                <th className="p-4 text-center">Actions</th>
              </tr>
            </thead>

            <tbody>
              {data?.items.map((admin) => {
                const joinedDate = new Date(admin.createdAtUtc);
                const isActive = admin.status === 'Active';

                return (
                  <tr
                    key={admin.id}
                    className="border-b dark:border-gray-800 hover:bg-gray-50 dark:hover:bg-gray-800/50"
                  >
                    <td className="p-4 font-medium dark:text-gray-200">
                      <div className="truncate">
                        {admin.firstName} {admin.lastName}
                      </div>
                      <div className="text-xs text-gray-500 truncate">
                        @{admin.userName}
                      </div>
                    </td>

                    <td className="p-4 dark:text-gray-400">
                      <div className="truncate">{admin.email}</div>
                    </td>

                    <td className="p-4">
                      <div className="flex justify-center">
                        <span
                          className={`inline-flex items-center justify-center min-w-[110px] px-2 py-1 text-xs rounded-full font-medium ${getStatusBadgeClasses(
                            admin.status
                          )}`}
                        >
                          {admin.status}
                        </span>
                      </div>
                    </td>

                    <td className="p-4 dark:text-gray-400">
                      <div className="text-center">
                        {joinedDate.toLocaleDateString()}
                      </div>
                      <div className="text-xs text-gray-500 dark:text-gray-500 mt-1 text-center">
                        {joinedDate.toLocaleTimeString([], {
                          hour: '2-digit',
                          minute: '2-digit',
                          second: '2-digit',
                        })}
                      </div>
                    </td>

                    <td className="p-4">
                      <div className="flex justify-center">
                        <button
                          type="button"
                          disabled={toggleStatusMutation.isPending}
                          onClick={() =>
                            toggleStatusMutation.mutate({
                              id: admin.id,
                              status: admin.status,
                            })
                          }
                          className={getActionButtonClasses(isActive)}
                        >
                          {isActive ? (
                            <>
                              <ShieldAlert size={16} />
                              Deactivate
                            </>
                          ) : (
                            <>
                              <ShieldCheck size={16} />
                              Reactivate
                            </>
                          )}
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}

              {data?.items.length === 0 && (
                <tr>
                  <td colSpan={5} className="p-8 text-center text-gray-500">
                    No admins found.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      <CreateAdminDrawer
        isOpen={isCreateDrawerOpen}
        onClose={() => setIsCreateDrawerOpen(false)}
        onCreated={handleAdminCreated}
      />
    </>
  );
};