import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  Circle,
  Search,
  Radio,
  Square,
  Trash2,
  Users as UsersIcon,
} from 'lucide-react';
import { useLiveSessions, useSessions } from '../hooks/useSessionQueries';
import { ForceEndSessionDialog } from './ForceEndSessionDialog';
import { DeleteSessionDialog } from './DeleteSessionDialog';
import type { SessionStatusValue } from '../types';

const PAGE_SIZE = 20;
type Tab = 'all' | 'live';

const statusBadge = (status: string) => {
  switch (status) {
    case 'Live':
      return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
    case 'Ended':
      return 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300';
    case 'Scheduled':
      return 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400';
    case 'PendingDeletion':
      return 'bg-amber-100 text-amber-800 dark:bg-amber-950/40 dark:text-amber-300';
    default:
      return 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300';
  }
};

const artifactBadge = (status?: string | null) => {
  switch (status) {
    case 'Available':
      return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
    case 'Failed':
      return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
    case 'Processing':
    case 'Generating':
      return 'bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400';
    default:
      return 'bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400';
  }
};

// Duration from the stored timestamps; a live session is measured up to now.
const formatDuration = (startedAt?: string | null, endedAt?: string | null) => {
  if (!startedAt) return '—';
  const start = new Date(startedAt).getTime();
  const end = endedAt ? new Date(endedAt).getTime() : Date.now();
  const minutes = Math.max(0, Math.round((end - start) / 60000));
  if (minutes < 60) return `${minutes}m`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
};

export const SessionsMonitorPage = () => {
  const { t } = useTranslation('superAdmin');

  const [tab, setTab] = useState<Tab>('all');
  const [searchText, setSearchText] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [status, setStatus] = useState<SessionStatusValue>('');
  const [page, setPage] = useState(1);
  const [forceEndTarget, setForceEndTarget] = useState<{ id: string; title: string } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<{ id: string; title: string } | null>(null);

  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(searchText.trim()), 300);
    return () => window.clearTimeout(id);
  }, [searchText]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, status]);

  const sessionsQuery = useSessions({
    search: debouncedSearch,
    status,
    page,
    pageSize: PAGE_SIZE,
  });
  const liveQuery = useLiveSessions(tab === 'live');

  const data = sessionsQuery.data;
  const sessions = data?.items ?? [];

  const tabButton = (value: Tab, label: string) => (
    <button
      type="button"
      onClick={() => setTab(value)}
      className={`rounded-lg px-4 py-2 text-sm font-medium transition-colors ${
        tab === value
          ? 'bg-violet-600 text-white'
          : 'border border-slate-200 text-slate-700 hover:bg-slate-100 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900'
      }`}
    >
      {label}
    </button>
  );

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
        <h1 className="text-3xl font-bold dark:text-white">{t('sessions.title')}</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{t('sessions.subtitle')}</p>
      </div>

      <div className="mb-6 flex gap-2">
        {tabButton('all', t('sessions.tabs.all'))}
        {tabButton('live', t('sessions.tabs.live'))}
      </div>

      {tab === 'all' ? (
        <>
          {/* Filters */}
          <div className="mb-6 grid gap-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900 md:grid-cols-[minmax(0,1fr)_220px]">
            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                {t('sessions.searchLabel')}
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
                  placeholder={t('sessions.searchPlaceholder')}
                  className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
                />
              </div>
            </div>
            <div>
              <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
                {t('sessions.statusLabel')}
              </label>
              <select
                value={status}
                onChange={(e) => setStatus(e.target.value as SessionStatusValue)}
                className="w-full appearance-none rounded-lg border border-slate-200 bg-white px-4 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
              >
                <option value="">{t('sessions.statuses.all')}</option>
                <option value="Scheduled">{t('sessions.statuses.scheduled')}</option>
                <option value="Live">{t('sessions.statuses.live')}</option>
                <option value="Ended">{t('sessions.statuses.ended')}</option>
              </select>
            </div>
          </div>

          {sessionsQuery.isLoading && !data ? (
            <div className="p-8 text-slate-500">{t('sessions.loading')}</div>
          ) : sessionsQuery.isError ? (
            <div className="p-8 text-red-500">{t('sessions.loadError')}</div>
          ) : (
            <>
              <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
                <table className="w-full table-fixed text-left text-sm">
                  <colgroup>
                    <col style={{ width: '26%' }} />
                    <col style={{ width: '20%' }} />
                    <col style={{ width: '12%' }} />
                    <col style={{ width: '14%' }} />
                    <col style={{ width: '18%' }} />
                    <col style={{ width: '10%' }} />
                  </colgroup>
                  <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
                    <tr>
                      <th className="p-4 text-center">{t('sessions.table.session')}</th>
                      <th className="p-4 text-center">{t('sessions.table.classroom')}</th>
                      <th className="p-4 text-center">{t('sessions.table.status')}</th>
                      <th className="p-4 text-center">{t('sessions.table.schedule')}</th>
                      <th className="p-4 text-center">{t('sessions.table.artifacts')}</th>
                      <th className="p-4 text-center">{t('sessions.table.actions')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sessions.length === 0 ? (
                      <tr>
                        <td colSpan={6} className="p-8 text-center text-gray-500">
                          {t('sessions.empty')}
                        </td>
                      </tr>
                    ) : (
                      sessions.map((s) => (
                        <tr key={s.sessionId} className="border-b dark:border-gray-800">
                          <td className="p-4 font-medium dark:text-gray-200">
                            <div className="truncate">{s.title}</div>
                            <div className="truncate text-xs text-gray-500">
                              {t('sessions.table.duration')}: {formatDuration(s.startedAtUtc, s.endedAtUtc)}
                            </div>
                          </td>
                          <td className="p-4 dark:text-gray-300">
                            <div className="truncate">{s.className}</div>
                            <div className="truncate text-xs text-gray-500">
                              {s.teacherName ?? t('sessions.table.unknownTeacher')}
                            </div>
                          </td>
                          <td className="p-4">
                            <div className="flex justify-center">
                              <span className={`inline-flex min-w-[80px] items-center justify-center rounded-full px-2 py-1 text-xs font-medium ${statusBadge(s.status)}`}>
                                {s.status === 'PendingDeletion' ? t('sessions.statuses.pendingDeletion') : s.status}
                              </span>
                            </div>
                          </td>
                          <td className="p-4 text-center text-xs dark:text-gray-400">
                            {new Date(s.scheduledAtUtc).toLocaleDateString()}
                            <div className="text-gray-500">
                              {new Date(s.scheduledAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                            </div>
                          </td>
                          <td className="p-4">
                            <div className="flex flex-col items-center gap-1">
                              <span className={`rounded-full px-2 py-0.5 text-[11px] ${artifactBadge(s.recordingStatus)}`}>
                                {t('sessions.table.recording')}: {s.recordingStatus ?? '—'}
                              </span>
                              <span className={`rounded-full px-2 py-0.5 text-[11px] ${artifactBadge(s.summaryStatus)}`}>
                                {t('sessions.table.summary')}: {s.summaryStatus ?? '—'}
                              </span>
                            </div>
                          </td>
                          <td className="p-4">
                            <div className="flex justify-center gap-2">
                              {s.status === 'Live' ? (
                                <button
                                  type="button"
                                  onClick={() => setForceEndTarget({ id: s.sessionId, title: s.title })}
                                  className="inline-flex items-center gap-1.5 rounded-md border border-red-200 bg-red-50 px-2.5 py-1.5 text-xs font-medium text-red-600 hover:bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400"
                                >
                                  <Square size={12} />
                                  {t('sessions.forceEnd.action')}
                                </button>
                              ) : (
                                <button
                                  type="button"
                                  onClick={() => setDeleteTarget({ id: s.sessionId, title: s.title })}
                                  className="inline-flex items-center gap-1.5 rounded-md border border-red-200 px-2.5 py-1.5 text-xs font-medium text-red-600 hover:bg-red-50 dark:border-red-900/50 dark:text-red-400 dark:hover:bg-red-950/30"
                                >
                                  <Trash2 size={12} />
                                  {s.status === 'PendingDeletion'
                                    ? t('sessions.delete.retry')
                                    : t('sessions.delete.action')}
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
                  <span>{t('sessions.pagination.showing', { count: sessions.length, total: data.totalCount })}</span>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() => setPage((p) => Math.max(1, p - 1))}
                      disabled={!data.hasPreviousPage || sessionsQuery.isFetching}
                      className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium hover:bg-slate-100 disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
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
                      disabled={!data.hasNextPage || sessionsQuery.isFetching}
                      className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium hover:bg-slate-100 disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
                    >
                      {t('users.pagination.next')}
                      <ChevronRight size={16} />
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </>
      ) : (
        /* Live view (step 4) */
        <>
          {liveQuery.data?.realtimeUnavailable && (
            <div className="mb-4 flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
              <AlertTriangle size={18} />
              {t('sessions.live.realtimeUnavailable')}
            </div>
          )}

          {liveQuery.isLoading ? (
            <div className="p-8 text-slate-500">{t('sessions.loading')}</div>
          ) : liveQuery.isError ? (
            <div className="p-8 text-red-500">{t('sessions.loadError')}</div>
          ) : (liveQuery.data?.items.length ?? 0) === 0 ? (
            <div className="rounded-lg border bg-white p-8 text-center text-gray-500 dark:border-gray-800 dark:bg-gray-900">
              {t('sessions.live.empty')}
            </div>
          ) : (
            <div className="grid gap-4 sm:grid-cols-2">
              {liveQuery.data!.items.map((s) => (
                <div
                  key={s.sessionId}
                  className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900"
                >
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <Radio size={14} className="animate-pulse text-red-500" />
                        <span className="truncate font-medium text-slate-900 dark:text-slate-100">
                          {s.title}
                        </span>
                      </div>
                      <div className="truncate text-xs text-slate-500">
                        {s.className} · {s.teacherName ?? t('sessions.table.unknownTeacher')}
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => setForceEndTarget({ id: s.sessionId, title: s.title })}
                      className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-red-200 bg-red-50 px-2.5 py-1.5 text-xs font-medium text-red-600 hover:bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400"
                    >
                      <Square size={12} />
                      {t('sessions.forceEnd.action')}
                    </button>
                  </div>

                  <div className="mt-4 grid grid-cols-3 gap-2 text-center text-xs">
                    <div className="rounded-lg bg-slate-50 p-2 dark:bg-slate-950/50">
                      <div className="flex items-center justify-center gap-1 text-slate-500">
                        <UsersIcon size={12} /> {t('sessions.live.participants')}
                      </div>
                      <div className="mt-1 font-semibold text-slate-800 dark:text-slate-200">
                        {s.participantCount ?? '—'}
                      </div>
                    </div>
                    <div className="rounded-lg bg-slate-50 p-2 dark:bg-slate-950/50">
                      <div className="text-slate-500">{t('sessions.live.recording')}</div>
                      <div className="mt-1 flex items-center justify-center gap-1 font-semibold">
                        <Circle
                          size={8}
                          className={s.isRecording ? 'fill-red-500 text-red-500' : 'fill-slate-300 text-slate-300'}
                        />
                        {s.isRecording == null ? '—' : t(s.isRecording ? 'sessions.live.on' : 'sessions.live.off')}
                      </div>
                    </div>
                    <div className="rounded-lg bg-slate-50 p-2 dark:bg-slate-950/50">
                      <div className="text-slate-500">{t('sessions.live.assistant')}</div>
                      <div className="mt-1 flex items-center justify-center gap-1 font-semibold">
                        <Circle
                          size={8}
                          className={s.assistantRunning ? 'fill-green-500 text-green-500' : 'fill-slate-300 text-slate-300'}
                        />
                        {s.assistantRunning == null ? '—' : t(s.assistantRunning ? 'sessions.live.on' : 'sessions.live.off')}
                      </div>
                    </div>
                  </div>

                  <div className="mt-3 text-center text-xs text-slate-500">
                    {t('sessions.table.duration')}: {formatDuration(s.startedAtUtc, null)}
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      )}

      <ForceEndSessionDialog
        sessionId={forceEndTarget?.id ?? null}
        sessionTitle={forceEndTarget?.title ?? ''}
        onClose={() => setForceEndTarget(null)}
      />

      <DeleteSessionDialog
        sessionId={deleteTarget?.id ?? null}
        sessionTitle={deleteTarget?.title ?? ''}
        onClose={() => setDeleteTarget(null)}
        onDeleted={() => setDeleteTarget(null)}
      />
    </div>
  );
};
