import { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  ArrowUpDown,
  Layers3,
  Search,
  ShieldAlert,
  ShieldCheck,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { getAdmins, toggleAdminStatus } from '../api/superAdmin';
import type { AdminQueryResult } from '../types';
import { CreateAdminDrawer } from './CreateAdminDrawer';

type SortOption =
  | 'joined-desc'
  | 'joined-asc'
  | 'name-asc'
  | 'name-desc'
  | 'status';

type GroupOption = 'none' | 'status';

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

const getStatusSortOrder = (status: string) => {
  switch (status) {
    case 'Active':
      return 0;
    case 'Pending':
      return 1;
    case 'Deactivated':
      return 2;
    case 'Rejected':
      return 3;
    default:
      return 99;
  }
};

const groupOrder = ['Active', 'Pending', 'Deactivated', 'Rejected'];

export const SuperAdminDashboard = () => {
  const { t } = useTranslation('admin');
  const queryClient = useQueryClient();
  const [isCreateDrawerOpen, setIsCreateDrawerOpen] = useState(false);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [searchTerm, setSearchTerm] = useState('');
  const [sortBy, setSortBy] = useState<SortOption>('joined-desc');
  const [groupBy, setGroupBy] = useState<GroupOption>('none');

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

  const allAdmins = data?.items ?? [];

  const filteredAndSortedAdmins = useMemo(() => {
    const normalizedSearch = searchTerm.trim().toLowerCase();

    const filtered = allAdmins.filter((admin) => {
      if (!normalizedSearch) {
        return true;
      }

      const fullName = `${admin.firstName} ${admin.lastName}`.toLowerCase();
      const username = admin.userName.toLowerCase();
      const email = admin.email.toLowerCase();
      const status = admin.status.toLowerCase();

      return (
        fullName.includes(normalizedSearch) ||
        username.includes(normalizedSearch) ||
        email.includes(normalizedSearch) ||
        status.includes(normalizedSearch)
      );
    });

    const sorted = [...filtered].sort((a, b) => {
      switch (sortBy) {
        case 'joined-desc':
          return (
            new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime()
          );

        case 'joined-asc':
          return (
            new Date(a.createdAtUtc).getTime() - new Date(b.createdAtUtc).getTime()
          );

        case 'name-asc': {
          const aName = `${a.firstName} ${a.lastName}`.trim().toLowerCase();
          const bName = `${b.firstName} ${b.lastName}`.trim().toLowerCase();
          return aName.localeCompare(bName);
        }

        case 'name-desc': {
          const aName = `${a.firstName} ${a.lastName}`.trim().toLowerCase();
          const bName = `${b.firstName} ${b.lastName}`.trim().toLowerCase();
          return bName.localeCompare(aName);
        }

        case 'status': {
          const statusCompare =
            getStatusSortOrder(a.status) - getStatusSortOrder(b.status);

          if (statusCompare !== 0) {
            return statusCompare;
          }

          return (
            new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime()
          );
        }

        default:
          return 0;
      }
    });

    return sorted;
  }, [allAdmins, searchTerm, sortBy]);

  const groupedAdmins = useMemo(() => {
    if (groupBy !== 'status') {
      return [];
    }

    const groupsMap = new Map<string, AdminQueryResult[]>();

    filteredAndSortedAdmins.forEach((admin) => {
      const existing = groupsMap.get(admin.status) ?? [];
      existing.push(admin);
      groupsMap.set(admin.status, existing);
    });

    const orderedGroups = groupOrder
      .filter((status) => groupsMap.has(status))
      .map((status) => ({
        label: status,
        items: groupsMap.get(status) ?? [],
      }));

    const extraGroups = Array.from(groupsMap.entries())
      .filter(([status]) => !groupOrder.includes(status))
      .map(([status, items]) => ({
        label: status,
        items,
      }));

    return [...orderedGroups, ...extraGroups];
  }, [filteredAndSortedAdmins, groupBy]);

  const handleAdminCreated = async (adminName: string) => {
    setIsCreateDrawerOpen(false);
    setSuccessMessage(t('dashboard.successCreated', { name: adminName }));
    await queryClient.invalidateQueries({ queryKey: ['admins'] });
  };

  const renderTable = (admins: AdminQueryResult[]) => {
    return (
      <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
        <table className="w-full table-fixed text-left text-sm">
          <colgroup>
            <col style={{ width: '24%' }} />
            <col style={{ width: '24%' }} />
            <col style={{ width: '14%' }} />
            <col style={{ width: '18%' }} />
            <col style={{ width: '20%' }} />
          </colgroup>

          <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
            <tr>
              <th className="p-4 text-center">{t('dashboard.table.name')}</th>
              <th className="p-4 text-center">{t('dashboard.table.email')}</th>
              <th className="p-4 text-center">{t('dashboard.table.status')}</th>
              <th className="p-4 text-center">{t('dashboard.table.joined')}</th>
              <th className="p-4 text-center">{t('dashboard.table.actions')}</th>
            </tr>
          </thead>

          <tbody>
            {admins.length === 0 ? (
              <tr>
                <td colSpan={5} className="p-8 text-center text-gray-500">
                  {t('dashboard.empty')}
                </td>
              </tr>
            ) : (
              admins.map((admin) => {
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
                      <div className="truncate text-xs text-gray-500">
                        @{admin.userName}
                      </div>
                    </td>

                    <td className="p-4 dark:text-gray-400">
                      <div className="truncate">{admin.email}</div>
                    </td>

                    <td className="p-4">
                      <div className="flex justify-center">
                        <span
                          className={`inline-flex min-w-[110px] items-center justify-center rounded-full px-2 py-1 text-xs font-medium ${getStatusBadgeClasses(
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
                      <div className="mt-1 text-center text-xs text-gray-500 dark:text-gray-500">
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
                              {t('dashboard.actions.deactivate')}
                            </>
                          ) : (
                            <>
                              <ShieldCheck size={16} />
                              {t('dashboard.actions.reactivate')}
                            </>
                          )}
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    );
  };

  if (isLoading) {
    return <div className="p-8">{t('dashboard.loading')}</div>;
  }

  if (isError) {
    return <div className="p-8 text-red-500">{t('dashboard.loadError')}</div>;
  }

  return (
    <>
      <div className="mx-auto w-full max-w-6xl p-6">
        <div className="mb-6 flex items-center justify-between gap-4">
          <h1 className="text-3xl font-bold dark:text-white">
            {t('dashboard.title')}
          </h1>

          <button
            type="button"
            onClick={() => {
              setSuccessMessage(null);
              setIsCreateDrawerOpen(true);
            }}
            className="rounded-md bg-purple-600 px-4 py-2 text-white transition-colors hover:bg-purple-700"
          >
            + {t('dashboard.createNewAdmin')}
          </button>
        </div>

        {successMessage && (
          <div className="mb-5 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-700 dark:border-green-900/50 dark:bg-green-950/30 dark:text-green-300">
            {successMessage}
          </div>
        )}

        <div className="mb-6 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
            <div className="w-full lg:max-w-md">
              <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                {t('dashboard.searchLabel')}
              </label>

              <div className="relative">
                <Search
                  size={18}
                  className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
                />
                <input
                  type="text"
                  value={searchTerm}
                  onChange={(event) => setSearchTerm(event.target.value)}
                  placeholder={t('dashboard.searchPlaceholder')}
                  className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100 dark:placeholder:text-slate-500"
                />
              </div>
            </div>

            <div className="flex flex-col gap-4 sm:flex-row sm:items-end">
              <div className="min-w-[220px]">
                <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                  {t('dashboard.sortLabel')}
                </label>

                <div className="relative">
                  <ArrowUpDown
                    size={16}
                    className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
                  />
                  <select
                    value={sortBy}
                    onChange={(event) => setSortBy(event.target.value as SortOption)}
                    className="w-full appearance-none rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition-colors focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
                  >
                    <option value="joined-desc">
                      {t('dashboard.sortOptions.joinedDesc')}
                    </option>
                    <option value="joined-asc">
                      {t('dashboard.sortOptions.joinedAsc')}
                    </option>
                    <option value="name-asc">
                      {t('dashboard.sortOptions.nameAsc')}
                    </option>
                    <option value="name-desc">
                      {t('dashboard.sortOptions.nameDesc')}
                    </option>
                    <option value="status">
                      {t('dashboard.sortOptions.status')}
                    </option>
                  </select>
                </div>
              </div>

              <div className="min-w-[220px]">
                <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                  {t('dashboard.groupLabel')}
                </label>

                <div className="relative">
                  <Layers3
                    size={16}
                    className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
                  />
                  <select
                    value={groupBy}
                    onChange={(event) =>
                      setGroupBy(event.target.value as GroupOption)
                    }
                    className="w-full appearance-none rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition-colors focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
                  >
                    <option value="none">{t('dashboard.groupOptions.none')}</option>
                    <option value="status">{t('dashboard.groupOptions.status')}</option>
                  </select>
                </div>
              </div>
            </div>
          </div>
        </div>

        {groupBy === 'none' ? (
          renderTable(filteredAndSortedAdmins)
        ) : filteredAndSortedAdmins.length === 0 ? (
          renderTable([])
        ) : (
          <div className="space-y-6">
            {groupedAdmins.map((group) => (
              <section key={group.label}>
                <div className="mb-3 flex items-center justify-between">
                  <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
                    {group.label}
                  </h2>
                  <span className="text-sm text-slate-500 dark:text-slate-400">
                    {t('dashboard.groupCount', { count: group.items.length })}
                  </span>
                </div>

                {renderTable(group.items)}
              </section>
            ))}
          </div>
        )}
      </div>

      <CreateAdminDrawer
        isOpen={isCreateDrawerOpen}
        onClose={() => setIsCreateDrawerOpen(false)}
        onCreated={handleAdminCreated}
      />
    </>
  );
};