import { useEffect, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  GraduationCap,
  Search,
  UserMinus,
  UserPlus,
} from 'lucide-react';
import { useClassroomMembers } from '../hooks/useMemberQueries';
import { AddMemberDialog } from './AddMemberDialog';
import { RemoveMemberDialog } from './RemoveMemberDialog';
import type { ClassroomMemberItem } from '../types';

const PAGE_SIZE = 20;

export const ClassroomMembersPage = () => {
  const { t } = useTranslation('superAdmin');
  const { id } = useParams<{ id: string }>();
  const location = useLocation();
  const classroomId = id ?? '';
  // The classroom name is passed via navigation state from the classrooms list; on a direct load
  // it may be absent, in which case a generic heading is used.
  const classroomName = (location.state as { name?: string } | null)?.name ?? '';

  const [searchText, setSearchText] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [page, setPage] = useState(1);
  const [addOpen, setAddOpen] = useState(false);
  const [removing, setRemoving] = useState<ClassroomMemberItem | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedSearch(searchText.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [searchText]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch]);

  const membersQuery = useClassroomMembers(classroomId, {
    search: debouncedSearch,
    page,
    pageSize: PAGE_SIZE,
  });

  const data = membersQuery.data;
  const members = data?.items ?? [];

  const handleAdded = (name: string) => {
    setSuccessMessage(t('members.addedMessage', { name }));
  };

  const handleRemoved = (name: string) => {
    setSuccessMessage(t('members.removedMessage', { name }));
  };

  const roleLabel = (member: ClassroomMemberItem) =>
    member.isTeacher ? t('members.roles.teacher') : t('members.roles.student');

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <Link
        to="/super-admin/classrooms"
        className="mb-4 inline-flex items-center gap-2 text-sm font-medium text-slate-600 hover:text-violet-600 dark:text-slate-400 dark:hover:text-violet-400"
      >
        <ArrowLeft size={16} />
        {t('members.backToClassrooms')}
      </Link>

      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold dark:text-white">{t('members.title')}</h1>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            {classroomName
              ? t('members.subtitleNamed', { name: classroomName })
              : t('members.subtitle')}
          </p>
        </div>
        <button
          type="button"
          onClick={() => {
            setSuccessMessage(null);
            setAddOpen(true);
          }}
          className="inline-flex items-center gap-2 rounded-md bg-purple-600 px-4 py-2 text-white transition-colors hover:bg-purple-700"
        >
          <UserPlus size={16} />
          {t('members.addStudent')}
        </button>
      </div>

      {successMessage && (
        <div className="mb-5 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-700 dark:border-green-900/50 dark:bg-green-950/30 dark:text-green-300">
          {successMessage}
        </div>
      )}

      <div className="mb-6 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('members.searchLabel')}
        </label>
        <div className="relative">
          <Search
            size={18}
            className="pointer-events-none absolute start-3 top-1/2 -translate-y-1/2 text-slate-400"
          />
          <input
            type="text"
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            placeholder={t('members.searchPlaceholder')}
            className="w-full rounded-lg border border-slate-200 bg-white py-2.5 ps-10 pe-4 text-sm text-slate-900 outline-none placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
          />
        </div>
      </div>

      {membersQuery.isLoading && !data ? (
        <div className="p-8 text-slate-500">{t('members.loading')}</div>
      ) : membersQuery.isError ? (
        <div className="p-8 text-red-500">{t('members.loadError')}</div>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
            <table className="w-full table-fixed text-start text-sm">
              <colgroup>
                <col style={{ width: '32%' }} />
                <col style={{ width: '30%' }} />
                <col style={{ width: '16%' }} />
                <col style={{ width: '12%' }} />
                <col style={{ width: '10%' }} />
              </colgroup>
              <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
                <tr>
                  <th className="p-4 text-center">{t('members.table.name')}</th>
                  <th className="p-4 text-center">{t('members.table.email')}</th>
                  <th className="p-4 text-center">{t('members.table.role')}</th>
                  <th className="p-4 text-center">{t('members.table.joined')}</th>
                  <th className="p-4 text-center">{t('members.table.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {members.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="p-8 text-center text-gray-500">
                      {t('members.empty')}
                    </td>
                  </tr>
                ) : (
                  members.map((m) => (
                    <tr key={m.userId} className="border-b dark:border-gray-800">
                      <td className="p-4 font-medium dark:text-gray-200">
                        <div className="flex items-center gap-2">
                          {m.isTeacher && <GraduationCap size={14} className="shrink-0 text-violet-500" />}
                          <span className="truncate">{m.name || t('members.table.unknownUser')}</span>
                        </div>
                      </td>
                      <td className="p-4 dark:text-gray-300">
                        <span className="truncate text-xs text-gray-500">{m.email || '—'}</span>
                      </td>
                      <td className="p-4 text-center">
                        <span
                          className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${
                            m.isTeacher
                              ? 'bg-violet-100 text-violet-800 dark:bg-violet-950/40 dark:text-violet-300'
                              : 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300'
                          }`}
                        >
                          {roleLabel(m)}
                        </span>
                      </td>
                      <td className="p-4 text-center dark:text-gray-400">
                        {m.joinedAtUtc ? new Date(m.joinedAtUtc).toLocaleDateString() : '—'}
                      </td>
                      <td className="p-4">
                        <div className="flex justify-center">
                          {/* 5هـ: the classroom teacher is not removable through member management. */}
                          {m.isTeacher ? (
                            <span className="text-xs text-gray-400">{t('members.table.owner')}</span>
                          ) : (
                            <button
                              type="button"
                              onClick={() => {
                                setSuccessMessage(null);
                                setRemoving(m);
                              }}
                              className="inline-flex items-center gap-1.5 rounded-md border border-red-200 px-3 py-1.5 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 dark:border-red-900/50 dark:text-red-400 dark:hover:bg-red-950/30"
                            >
                              <UserMinus size={14} />
                              {t('members.table.remove')}
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          {data && data.totalCount > 0 && (
            <div className="mt-4 flex items-center justify-between text-sm text-slate-600 dark:text-slate-400">
              <span>
                {t('members.pagination.showing', { count: members.length, total: data.totalCount })}
              </span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={!data.hasPreviousPage || membersQuery.isFetching}
                  className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
                >
                  <ChevronLeft size={16} />
                  {t('users.pagination.previous')}
                </button>
                <span className="px-2">
                  {t('users.pagination.page', { page: data.pageNumber, total: Math.max(1, data.totalPages) })}
                </span>
                <button
                  type="button"
                  onClick={() => setPage((p) => p + 1)}
                  disabled={!data.hasNextPage || membersQuery.isFetching}
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

      <AddMemberDialog
        classroomId={classroomId}
        isOpen={addOpen}
        onClose={() => setAddOpen(false)}
        onAdded={handleAdded}
      />

      <RemoveMemberDialog
        classroomId={classroomId}
        member={removing}
        onClose={() => setRemoving(null)}
        onRemoved={handleRemoved}
      />
    </div>
  );
};
