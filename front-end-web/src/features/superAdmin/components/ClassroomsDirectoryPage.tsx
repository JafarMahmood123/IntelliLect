import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  FileText,
  PencilLine,
  Presentation,
  Search,
  Trash2,
  UserCog,
  Users as UsersIcon,
} from 'lucide-react';
import { useClassrooms } from '../hooks/useClassroomQueries';
import { ClassroomFormDrawer } from './ClassroomFormDrawer';
import { ChangeTeacherDialog } from './ChangeTeacherDialog';
import { DeleteClassroomDialog } from './DeleteClassroomDialog';
import type { ClassroomAdminItem } from '../types';

const PAGE_SIZE = 20;

export const ClassroomsDirectoryPage = () => {
  const { t } = useTranslation('superAdmin');

  const [searchText, setSearchText] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [page, setPage] = useState(1);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editing, setEditing] = useState<ClassroomAdminItem | null>(null);
  const [changingTeacher, setChangingTeacher] = useState<ClassroomAdminItem | null>(null);
  const [deleting, setDeleting] = useState<ClassroomAdminItem | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(searchText.trim()), 300);
    return () => window.clearTimeout(id);
  }, [searchText]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch]);

  const classroomsQuery = useClassrooms({
    search: debouncedSearch,
    page,
    pageSize: PAGE_SIZE,
  });

  const data = classroomsQuery.data;
  const classrooms = data?.items ?? [];

  const openCreate = () => {
    setEditing(null);
    setSuccessMessage(null);
    setDrawerOpen(true);
  };

  const openEdit = (classroom: ClassroomAdminItem) => {
    setEditing(classroom);
    setSuccessMessage(null);
    setDrawerOpen(true);
  };

  const handleSaved = (name: string) => {
    setDrawerOpen(false);
    setSuccessMessage(
      editing ? t('classrooms.savedEdit', { name }) : t('classrooms.savedCreate', { name }),
    );
  };

  const handleTeacherChanged = (name: string) => {
    setSuccessMessage(t('classrooms.changeTeacher.changedMessage', { name }));
  };

  const handleDeleted = (name: string) => {
    setSuccessMessage(t('classrooms.deletedMessage', { name }));
  };

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <Link
        to="/super-admin"
        className="mb-4 inline-flex items-center gap-2 text-sm font-medium text-slate-600 hover:text-violet-600 dark:text-slate-400 dark:hover:text-violet-400"
      >
        <ArrowLeft size={16} />
        {t('users.backToDashboard')}
      </Link>

      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold dark:text-white">{t('classrooms.title')}</h1>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{t('classrooms.subtitle')}</p>
        </div>
        <button
          type="button"
          onClick={openCreate}
          className="rounded-md bg-purple-600 px-4 py-2 text-white transition-colors hover:bg-purple-700"
        >
          + {t('classrooms.createNew')}
        </button>
      </div>

      {successMessage && (
        <div className="mb-5 rounded-lg border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-700 dark:border-green-900/50 dark:bg-green-950/30 dark:text-green-300">
          {successMessage}
        </div>
      )}

      <div className="mb-6 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('classrooms.searchLabel')}
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
            placeholder={t('classrooms.searchPlaceholder')}
            className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
          />
        </div>
      </div>

      {classroomsQuery.isLoading && !data ? (
        <div className="p-8 text-slate-500">{t('classrooms.loading')}</div>
      ) : classroomsQuery.isError ? (
        <div className="p-8 text-red-500">{t('classrooms.loadError')}</div>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
            <table className="w-full table-fixed text-left text-sm">
              <colgroup>
                <col style={{ width: '30%' }} />
                <col style={{ width: '22%' }} />
                <col style={{ width: '22%' }} />
                <col style={{ width: '14%' }} />
                <col style={{ width: '12%' }} />
              </colgroup>
              <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
                <tr>
                  <th className="p-4 text-center">{t('classrooms.table.name')}</th>
                  <th className="p-4 text-center">{t('classrooms.table.teacher')}</th>
                  <th className="p-4 text-center">{t('classrooms.table.counts')}</th>
                  <th className="p-4 text-center">{t('classrooms.table.created')}</th>
                  <th className="p-4 text-center">{t('classrooms.table.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {classrooms.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="p-8 text-center text-gray-500">
                      {t('classrooms.empty')}
                    </td>
                  </tr>
                ) : (
                  classrooms.map((c) => {
                    const pendingDeletion = c.status === 'PendingDeletion';
                    return (
                    <tr
                      key={c.id}
                      className={`border-b dark:border-gray-800 ${pendingDeletion ? 'opacity-60' : ''}`}
                    >
                      <td className="p-4 font-medium dark:text-gray-200">
                        <div className="flex items-center gap-2">
                          <span className="truncate">{c.name}</span>
                          {pendingDeletion && (
                            <span className="shrink-0 rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-800 dark:bg-amber-950/40 dark:text-amber-300">
                              {t('classrooms.pendingDeletion')}
                            </span>
                          )}
                        </div>
                        <div className="truncate text-xs text-gray-500">{c.description}</div>
                      </td>
                      <td className="p-4 dark:text-gray-300">
                        {c.teacherName ? (
                          <>
                            <div className="truncate">{c.teacherName}</div>
                            <div className="truncate text-xs text-gray-500">{c.teacherEmail}</div>
                          </>
                        ) : (
                          <span className="text-xs text-gray-400">{t('classrooms.table.unknownTeacher')}</span>
                        )}
                      </td>
                      <td className="p-4">
                        <div className="flex items-center justify-center gap-4 text-xs text-slate-500 dark:text-slate-400">
                          <span className="inline-flex items-center gap-1" title={t('classrooms.table.students')}>
                            <UsersIcon size={14} /> {c.studentCount}
                          </span>
                          <span className="inline-flex items-center gap-1" title={t('classrooms.table.sessions')}>
                            <Presentation size={14} /> {c.sessionCount}
                          </span>
                          <span className="inline-flex items-center gap-1" title={t('classrooms.table.files')}>
                            <FileText size={14} /> {c.fileCount}
                          </span>
                        </div>
                      </td>
                      <td className="p-4 text-center dark:text-gray-400">
                        {new Date(c.createdAtUtc).toLocaleDateString()}
                      </td>
                      <td className="p-4">
                        <div className="flex justify-center gap-2">
                          <button
                            type="button"
                            onClick={() => openEdit(c)}
                            disabled={pendingDeletion}
                            className="inline-flex items-center gap-1.5 rounded-md border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-700 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                          >
                            <PencilLine size={14} />
                            {t('classrooms.table.edit')}
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setSuccessMessage(null);
                              setChangingTeacher(c);
                            }}
                            disabled={pendingDeletion}
                            className="inline-flex items-center gap-1.5 rounded-md border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-700 transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                          >
                            <UserCog size={14} />
                            {t('classrooms.table.changeTeacher')}
                          </button>
                          <button
                            type="button"
                            onClick={() => {
                              setSuccessMessage(null);
                              setDeleting(c);
                            }}
                            className="inline-flex items-center gap-1.5 rounded-md border border-red-200 px-3 py-1.5 text-xs font-medium text-red-600 transition-colors hover:bg-red-50 dark:border-red-900/50 dark:text-red-400 dark:hover:bg-red-950/30"
                          >
                            <Trash2 size={14} />
                            {pendingDeletion ? t('classrooms.table.retryDelete') : t('classrooms.table.delete')}
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

          {data && data.totalCount > 0 && (
            <div className="mt-4 flex items-center justify-between text-sm text-slate-600 dark:text-slate-400">
              <span>
                {t('classrooms.pagination.showing', { count: classrooms.length, total: data.totalCount })}
              </span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={!data.hasPreviousPage || classroomsQuery.isFetching}
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
                  disabled={!data.hasNextPage || classroomsQuery.isFetching}
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

      <ClassroomFormDrawer
        isOpen={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        classroom={editing}
        onSaved={handleSaved}
      />

      <ChangeTeacherDialog
        classroom={changingTeacher}
        onClose={() => setChangingTeacher(null)}
        onChanged={handleTeacherChanged}
      />

      <DeleteClassroomDialog
        classroomId={deleting?.id ?? null}
        classroomName={deleting?.name ?? ''}
        onClose={() => setDeleting(null)}
        onDeleted={handleDeleted}
      />
    </div>
  );
};
