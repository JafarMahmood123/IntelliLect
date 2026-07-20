import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  ArrowLeft,
  ArrowUpDown,
  ChevronLeft,
  ChevronRight,
  Search,
} from 'lucide-react';
import { useUsers } from '../hooks/useUserQueries';
import { UserStatusActions } from './UserStatusActions';
import type {
  SearchUsersParams,
  SortDirection,
  UserRoleValue,
  UserStatusValue,
} from '../types';

type SortOption =
  | 'joined-desc'
  | 'joined-asc'
  | 'username-asc'
  | 'username-desc'
  | 'email-asc'
  | 'email-desc'
  | 'role-asc'
  | 'role-desc'
  | 'status-asc'
  | 'status-desc';

const PAGE_SIZE = 20;

const mapSort = (
  sort: SortOption,
): { sortBy: SearchUsersParams['sortBy']; sortDirection: SortDirection } => {
  const [field, direction] = sort.split('-') as [string, SortDirection];
  const sortByMap: Record<string, SearchUsersParams['sortBy']> = {
    joined: 'createdat',
    username: 'username',
    email: 'email',
    role: 'role',
    status: 'status',
  };
  return { sortBy: sortByMap[field], sortDirection: direction };
};

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

const selectClasses =
  'w-full appearance-none rounded-lg border border-slate-200 bg-white px-4 py-2.5 text-sm text-slate-900 outline-none transition-colors focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100';

export const UsersDirectoryPage = () => {
  const { t } = useTranslation('superAdmin');
  const navigate = useNavigate();

  const [searchText, setSearchText] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [role, setRole] = useState<UserRoleValue>('');
  const [status, setStatus] = useState<UserStatusValue>('');
  const [createdFrom, setCreatedFrom] = useState('');
  const [createdTo, setCreatedTo] = useState('');
  const [sort, setSort] = useState<SortOption>('joined-desc');
  const [page, setPage] = useState(1);

  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(searchText.trim()), 300);
    return () => window.clearTimeout(id);
  }, [searchText]);

  // Any filter change returns to the first page so results stay consistent.
  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, role, status, createdFrom, createdTo, sort]);

  const backendSort = useMemo(() => mapSort(sort), [sort]);

  const usersQuery = useUsers({
    searchTerm: debouncedSearch,
    role,
    status,
    createdFrom: createdFrom ? new Date(createdFrom).toISOString() : undefined,
    createdTo: createdTo ? new Date(createdTo).toISOString() : undefined,
    page,
    pageSize: PAGE_SIZE,
    sortBy: backendSort.sortBy,
    sortDirection: backendSort.sortDirection,
  });

  const hasFilters =
    debouncedSearch.length > 0 ||
    role !== '' ||
    status !== '' ||
    createdFrom !== '' ||
    createdTo !== '';

  const clearFilters = () => {
    setSearchText('');
    setDebouncedSearch('');
    setRole('');
    setStatus('');
    setCreatedFrom('');
    setCreatedTo('');
  };

  const data = usersQuery.data;
  const users = data?.items ?? [];

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <Link
        to="/super-admin"
        className="mb-4 inline-flex items-center gap-2 text-sm font-medium text-slate-600 hover:text-violet-600 dark:text-slate-400 dark:hover:text-violet-400"
      >
        <ArrowLeft size={16} />
        {t('users.backToDashboard')}
      </Link>

      <div className="mb-6">
        <h1 className="text-3xl font-bold dark:text-white">{t('users.title')}</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          {t('users.subtitle')}
        </p>
      </div>

      {/* Filters */}
      <div className="mb-6 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          <div className="xl:col-span-2">
            <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('users.search.label')}
            </label>
            <div className="relative">
              <Search
                size={18}
                className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
              />
              <input
                type="text"
                value={searchText}
                onChange={(e) => setSearchText(e.target.value)}
                placeholder={t('users.search.placeholder')}
                className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition-colors placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
              />
            </div>
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('users.filters.role')}
            </label>
            <select
              value={role}
              onChange={(e) => setRole(e.target.value as UserRoleValue)}
              className={selectClasses}
            >
              <option value="">{t('users.filters.allRoles')}</option>
              <option value="Student">{t('users.roles.student')}</option>
              <option value="Teacher">{t('users.roles.teacher')}</option>
              <option value="Admin">{t('users.roles.admin')}</option>
              <option value="SuperAdmin">{t('users.roles.superAdmin')}</option>
            </select>
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('users.filters.status')}
            </label>
            <select
              value={status}
              onChange={(e) => setStatus(e.target.value as UserStatusValue)}
              className={selectClasses}
            >
              <option value="">{t('users.filters.allStatuses')}</option>
              <option value="Active">{t('users.statuses.active')}</option>
              <option value="Pending">{t('users.statuses.pending')}</option>
              <option value="Rejected">{t('users.statuses.rejected')}</option>
              <option value="Deactivated">{t('users.statuses.deactivated')}</option>
            </select>
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('users.filters.createdFrom')}
            </label>
            <input
              type="date"
              value={createdFrom}
              onChange={(e) => setCreatedFrom(e.target.value)}
              className={selectClasses}
            />
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('users.filters.createdTo')}
            </label>
            <input
              type="date"
              value={createdTo}
              onChange={(e) => setCreatedTo(e.target.value)}
              className={selectClasses}
            />
          </div>

          <div>
            <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('users.sortLabel')}
            </label>
            <div className="relative">
              <ArrowUpDown
                size={16}
                className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"
              />
              <select
                value={sort}
                onChange={(e) => setSort(e.target.value as SortOption)}
                className={`${selectClasses} pl-10`}
              >
                <option value="joined-desc">{t('users.sortOptions.joinedDesc')}</option>
                <option value="joined-asc">{t('users.sortOptions.joinedAsc')}</option>
                <option value="username-asc">{t('users.sortOptions.usernameAsc')}</option>
                <option value="username-desc">{t('users.sortOptions.usernameDesc')}</option>
                <option value="email-asc">{t('users.sortOptions.emailAsc')}</option>
                <option value="email-desc">{t('users.sortOptions.emailDesc')}</option>
                <option value="role-asc">{t('users.sortOptions.roleAsc')}</option>
                <option value="role-desc">{t('users.sortOptions.roleDesc')}</option>
                <option value="status-asc">{t('users.sortOptions.statusAsc')}</option>
                <option value="status-desc">{t('users.sortOptions.statusDesc')}</option>
              </select>
            </div>
          </div>

          <div className="flex items-end">
            <button
              type="button"
              onClick={clearFilters}
              disabled={!hasFilters}
              className="inline-flex h-[42px] w-full items-center justify-center rounded-lg border border-slate-200 px-4 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
            >
              {t('users.filters.clear')}
            </button>
          </div>
        </div>
      </div>

      {usersQuery.isLoading && !data ? (
        <div className="p-8 text-slate-500">{t('users.loading')}</div>
      ) : usersQuery.isError ? (
        <div className="p-8 text-red-500">{t('users.loadError')}</div>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
            <table className="w-full table-fixed text-left text-sm">
              <colgroup>
                <col style={{ width: '22%' }} />
                <col style={{ width: '22%' }} />
                <col style={{ width: '12%' }} />
                <col style={{ width: '12%' }} />
                <col style={{ width: '14%' }} />
                <col style={{ width: '18%' }} />
              </colgroup>
              <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
                <tr>
                  <th className="p-4 text-center">{t('users.table.name')}</th>
                  <th className="p-4 text-center">{t('users.table.email')}</th>
                  <th className="p-4 text-center">{t('users.table.role')}</th>
                  <th className="p-4 text-center">{t('users.table.status')}</th>
                  <th className="p-4 text-center">{t('users.table.joined')}</th>
                  <th className="p-4 text-center">{t('users.table.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {users.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="p-8 text-center text-gray-500">
                      {t('users.empty')}
                    </td>
                  </tr>
                ) : (
                  users.map((user) => {
                    const joined = new Date(user.createdAtUtc);
                    return (
                      <tr
                        key={user.id}
                        onClick={() => navigate(`/super-admin/users/${user.id}`)}
                        className="cursor-pointer border-b transition-colors hover:bg-violet-50 dark:border-gray-800 dark:hover:bg-violet-950/20"
                      >
                        <td className="p-4 font-medium dark:text-gray-200">
                          <div className="truncate">
                            {user.firstName} {user.lastName}
                          </div>
                          <div className="truncate text-xs text-gray-500">
                            @{user.userName}
                          </div>
                        </td>
                        <td className="p-4 dark:text-gray-400">
                          <div className="truncate">{user.email}</div>
                        </td>
                        <td className="p-4 text-center dark:text-gray-300">
                          {user.roleName}
                        </td>
                        <td className="p-4">
                          <div className="flex justify-center">
                            <span
                              className={`inline-flex min-w-[100px] items-center justify-center rounded-full px-2 py-1 text-xs font-medium ${getStatusBadgeClasses(
                                user.status,
                              )}`}
                            >
                              {user.status}
                            </span>
                          </div>
                        </td>
                        <td className="p-4 text-center dark:text-gray-400">
                          {joined.toLocaleDateString()}
                        </td>
                        <td
                          className="p-4"
                          onClick={(e) => e.stopPropagation()}
                        >
                          <UserStatusActions userId={user.id} status={user.status} size="sm" />
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          {data && data.totalCount > 0 && (
            <div className="mt-4 flex items-center justify-between text-sm text-slate-600 dark:text-slate-400">
              <span>
                {t('users.pagination.showing', {
                  count: users.length,
                  total: data.totalCount,
                })}
              </span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={!data.hasPreviousPage || usersQuery.isFetching}
                  className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
                >
                  <ChevronLeft size={16} />
                  {t('users.pagination.previous')}
                </button>
                <span className="px-2">
                  {t('users.pagination.page', {
                    page: data.pageNumber,
                    total: Math.max(1, data.totalPages),
                  })}
                </span>
                <button
                  type="button"
                  onClick={() => setPage((p) => p + 1)}
                  disabled={!data.hasNextPage || usersQuery.isFetching}
                  className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
                >
                  {t('users.pagination.next')}
                  <ChevronRight size={16} />
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
};
