import { useEffect, useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  ArrowUpDown,
  Layers3,
  Search,
  ShieldAlert,
  ShieldCheck,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import {
  useAdmins,
  useToggleAdminStatus,
  type AdminsQueryData,
} from '../hooks/useSuperAdminQueries';
import type {
  AdminQueryResult,
  AdminSortField,
  SortDirection,
} from '../types';
import { CreateAdminDrawer } from './CreateAdminDrawer';

type SortOption =
  | 'joined-desc'
  | 'joined-asc'
  | 'username-asc'
  | 'username-desc'
  | 'email-asc'
  | 'email-desc'
  | 'firstName-asc'
  | 'firstName-desc'
  | 'lastName-asc'
  | 'lastName-desc'
  | 'status-asc'
  | 'status-desc';

type GroupOption = 'none' | 'status';
type SearchField = 'userName' | 'email' | 'firstName' | 'lastName' | 'status';
type AdminStatusValue = '' | 'Active' | 'Pending' | 'Rejected' | 'Deactivated';

const PAGE_SIZE = 50;

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

const mapSortOptionToBackend = (
  sortOption: SortOption,
): { sortBy: AdminSortField; sortDirection: SortDirection } => {
  switch (sortOption) {
    case 'joined-asc': return { sortBy: 'createdat', sortDirection: 'asc' };
    case 'joined-desc': return { sortBy: 'createdat', sortDirection: 'desc' };
    case 'username-asc': return { sortBy: 'username', sortDirection: 'asc' };
    case 'username-desc': return { sortBy: 'username', sortDirection: 'desc' };
    case 'email-asc': return { sortBy: 'email', sortDirection: 'asc' };
    case 'email-desc': return { sortBy: 'email', sortDirection: 'desc' };
    case 'firstName-asc': return { sortBy: 'firstname', sortDirection: 'asc' };
    case 'firstName-desc': return { sortBy: 'firstname', sortDirection: 'desc' };
    case 'lastName-asc': return { sortBy: 'lastname', sortDirection: 'asc' };
    case 'lastName-desc': return { sortBy: 'lastname', sortDirection: 'desc' };
    case 'status-asc': return { sortBy: 'status', sortDirection: 'asc' };
    case 'status-desc': return { sortBy: 'status', sortDirection: 'desc' };
    default: return { sortBy: 'createdat', sortDirection: 'desc' };
  }
};

const getSearchPlaceholder = (searchField: SearchField) => {
  switch (searchField) {
    case 'userName':
      return 'dashboard.placeholders.userName';
    case 'email':
      return 'dashboard.placeholders.email';
    case 'firstName':
      return 'dashboard.placeholders.firstName';
    case 'lastName':
      return 'dashboard.placeholders.lastName';
    case 'status':
      return 'dashboard.placeholders.status';
    default:
      return 'dashboard.searchLabel';
  }
};

const getPagedItems = (result: AdminsQueryData | undefined) => {
  if (!result) return [];
  if (result.mode === 'grouped') {
    return result.data.groups.flatMap((group) => group.items);
  }
  return result.data.items;
};

export const SuperAdminDashboard = () => {
  const { t } = useTranslation('superAdmin');
  const queryClient = useQueryClient();

  const [isCreateDrawerOpen, setIsCreateDrawerOpen] = useState(false);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const [searchField, setSearchField] = useState<SearchField>('userName');
  const [searchText, setSearchText] = useState('');
  const [debouncedSearchText, setDebouncedSearchText] = useState('');
  const [statusSearch, setStatusSearch] = useState<AdminStatusValue>('');

  const [sortBy, setSortBy] = useState<SortOption>('joined-desc');
  const [groupBy, setGroupBy] = useState<GroupOption>('none');

  useEffect(() => {
    const timeoutId = window.setTimeout(() => {
      setDebouncedSearchText(searchText.trim());
    }, 250);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [searchText]);

  const hasActiveSearch =
    searchField === 'status'
      ? statusSearch !== ''
      : debouncedSearchText.trim().length > 0;

  const backendSort = useMemo(() => mapSortOptionToBackend(sortBy), [sortBy]);

  const adminsQuery = useAdmins({
    hasActiveSearch,
    groupBy,
    searchField,
    debouncedSearchText,
    statusSearch,
    sortBy: backendSort.sortBy,
    sortDirection: backendSort.sortDirection,
    pageSize: PAGE_SIZE,
  });

  const toggleStatusMutation = useToggleAdminStatus();

  const handleAdminCreated = async (adminName: string) => {
    setIsCreateDrawerOpen(false);
    setSuccessMessage(t('dashboard.successCreated', { name: adminName }));
    await queryClient.invalidateQueries({ queryKey: ['admins'] });
  };

  const clearSearch = () => {
    setSearchText('');
    setDebouncedSearchText('');
    setStatusSearch('');
  };

  const disableGroupingForSearch = () => {
    if (groupBy === 'status') {
      setGroupBy('none');
    }
  };

  const handleSearchFieldChange = (value: SearchField) => {
    setSearchField(value);
    setSearchText('');
    setDebouncedSearchText('');
    setStatusSearch('');
    disableGroupingForSearch();
  };

  const handleSearchTextChange = (value: string) => {
    setSearchText(value);
    if (value.trim().length > 0) {
      disableGroupingForSearch();
    }
  };

  const handleStatusSearchChange = (value: AdminStatusValue) => {
    setStatusSearch(value);
    if (value !== '') {
      disableGroupingForSearch();
    }
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
                            admin.status,
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

  const showInitialLoading = adminsQuery.isLoading && !adminsQuery.data;
  const showInitialError = adminsQuery.isError && !adminsQuery.data;
  const isRefreshingResults = adminsQuery.isFetching && !!adminsQuery.data;

  if (showInitialLoading) {
    return <div className="p-8">{t('dashboard.loading')}</div>;
  }

  if (showInitialError || !adminsQuery.data) {
    return <div className="p-8 text-red-500">{t('dashboard.loadError')}</div>;
  }

  const flatItems = getPagedItems(adminsQuery.data);

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
          <div className="flex flex-col gap-4">
            <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
              <div className="grid w-full gap-4 md:grid-cols-[220px_minmax(0,1fr)] xl:max-w-3xl">
                <div>
                  <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                    {t('dashboard.searchByLabel')}
                  </label>

                  <div className="relative">
                    <Search
                      size={16}
                      className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
                    />
                    <select
                      value={searchField}
                      onChange={(event) =>
                        handleSearchFieldChange(event.target.value as SearchField)
                      }
                      className="w-full appearance-none rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition-colors focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
                    >
                      <option value="userName">{t('dashboard.searchFields.userName')}</option>
                      <option value="email">{t('dashboard.searchFields.email')}</option>
                      <option value="firstName">{t('dashboard.searchFields.firstName')}</option>
                      <option value="lastName">{t('dashboard.searchFields.lastName')}</option>
                      <option value="status">{t('dashboard.searchFields.status')}</option>
                    </select>
                  </div>
                </div>

                <div>
                  <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                    {t('dashboard.searchLabel')}
                  </label>

                  {searchField === 'status' ? (
                    <select
                      value={statusSearch}
                      onChange={(event) =>
                        handleStatusSearchChange(
                          event.target.value as AdminStatusValue,
                        )
                      }
                      className="w-full appearance-none rounded-lg border border-slate-200 bg-white px-4 py-2.5 text-sm text-slate-900 outline-none transition-colors focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
                    >
                      <option value="">{t(getSearchPlaceholder(searchField))}</option>
                      <option value="Active">{t('dashboard.statuses.active')}</option>
                      <option value="Pending">{t('dashboard.statuses.pending')}</option>
                      <option value="Rejected">{t('dashboard.statuses.rejected')}</option>
                      <option value="Deactivated">
                        {t('dashboard.statuses.deactivated')}
                      </option>
                    </select>
                  ) : (
                    <div className="relative">
                      <Search
                        size={18}
                        className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
                      />
                      <input
                        type="text"
                        value={searchText}
                        onChange={(event) =>
                          handleSearchTextChange(event.target.value)
                        }
                        placeholder={t(getSearchPlaceholder(searchField))}
                        className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100 dark:placeholder:text-slate-500"
                      />
                    </div>
                  )}
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
                      <option value="joined-desc">{t('dashboard.sortOptions.joinedDesc')}</option>
                      <option value="joined-asc">{t('dashboard.sortOptions.joinedAsc')}</option>
                      <option value="username-asc">{t('dashboard.sortOptions.usernameAsc')}</option>
                      <option value="username-desc">{t('dashboard.sortOptions.usernameDesc')}</option>
                      <option value="email-asc">{t('dashboard.sortOptions.emailAsc')}</option>
                      <option value="email-desc">{t('dashboard.sortOptions.emailDesc')}</option>
                      <option value="firstName-asc">{t('dashboard.sortOptions.firstNameAsc')}</option>
                      <option value="firstName-desc">{t('dashboard.sortOptions.firstNameDesc')}</option>
                      <option value="lastName-asc">{t('dashboard.sortOptions.lastNameAsc')}</option>
                      <option value="lastName-desc">{t('dashboard.sortOptions.lastNameDesc')}</option>
                      <option value="status-asc">{t('dashboard.sortOptions.statusAsc')}</option>
                      <option value="status-desc">{t('dashboard.sortOptions.statusDesc')}</option>
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
                      <option value="status" disabled={hasActiveSearch}>
                        {hasActiveSearch
                          ? t('dashboard.groupOptions.statusDisabled')
                          : t('dashboard.groupOptions.status')}
                      </option>
                    </select>
                  </div>
                </div>

                <button
                  type="button"
                  onClick={clearSearch}
                  disabled={!hasActiveSearch}
                  className="inline-flex h-[42px] items-center justify-center rounded-lg border border-slate-200 px-4 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
                >
                  {t('dashboard.clearSearch')}
                </button>
              </div>
            </div>

            {isRefreshingResults && (
              <div className="text-sm text-slate-500 dark:text-slate-400">
                {t('dashboard.updatingResults')}
              </div>
            )}

            {hasActiveSearch && (
              <div className="rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-300">
                {t('dashboard.searchGroupingNotice')}
              </div>
            )}
          </div>
        </div>

        {adminsQuery.data.mode === 'grouped' ? (
          adminsQuery.data.data.groups.length === 0 ? (
            renderTable([])
          ) : (
            <div className="space-y-6">
              {adminsQuery.data.data.groups.map((group) => (
                <section key={group.status} className="space-y-3">
                  <div className="flex items-center justify-between gap-4">
                    <div className="flex items-center gap-3">
                      <span
                        className={`inline-flex min-w-[110px] items-center justify-center rounded-full px-3 py-1 text-xs font-semibold ${getStatusBadgeClasses(
                          group.status,
                        )}`}
                      >
                        {group.status}
                      </span>
                      <span className="text-sm text-slate-500 dark:text-slate-400">
                        {t('dashboard.groupCount', {
                          count: group.items.length,
                        })}
                      </span>
                    </div>
                  </div>

                  {renderTable(group.items)}
                </section>
              ))}
            </div>
          )
        ) : (
          renderTable(flatItems)
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
