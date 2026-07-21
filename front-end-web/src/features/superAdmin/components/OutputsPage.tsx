import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  FileVideo,
  ScrollText,
  Search,
  Trash2,
} from 'lucide-react';
import { useOutputs } from '../hooks/useOutputQueries';
import { DeleteOutputDialog } from './DeleteOutputDialog';
import type { OutputItem, OutputStatusValue, OutputTypeValue } from '../types';

const PAGE_SIZE = 20;

const formatBytes = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / Math.pow(1024, exponent)).toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
};

const statusBadge = (status: string): string => {
  switch (status) {
    case 'Available':
      return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
    case 'Failed':
      return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
    case 'Processing':
    case 'Generating':
      return 'bg-amber-100 text-amber-800 dark:bg-amber-950/40 dark:text-amber-300';
    case 'PendingDeletion':
      return 'bg-orange-100 text-orange-800 dark:bg-orange-950/40 dark:text-orange-300';
    default:
      return 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400';
  }
};

export const OutputsPage = () => {
  const { t } = useTranslation('superAdmin');

  const [searchText, setSearchText] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [type, setType] = useState<OutputTypeValue>('');
  const [status, setStatus] = useState<OutputStatusValue>('');
  const [page, setPage] = useState(1);
  const [deleteTarget, setDeleteTarget] = useState<OutputItem | null>(null);

  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(searchText.trim()), 300);
    return () => window.clearTimeout(id);
  }, [searchText]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, type, status]);

  const outputsQuery = useOutputs({
    search: debouncedSearch,
    type,
    status,
    page,
    pageSize: PAGE_SIZE,
  });

  const data = outputsQuery.data;
  const outputs = data?.items ?? [];
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1;

  const statusLabel = (s: string) =>
    s === 'PendingDeletion' ? t('outputs.statuses.pendingDeletion') : s;

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
        <h1 className="text-3xl font-bold dark:text-white">{t('outputs.title')}</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{t('outputs.subtitle')}</p>
      </div>

      {/* Filters */}
      <div className="mb-6 flex flex-wrap items-end gap-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="min-w-[220px] flex-1">
          <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
            {t('outputs.searchLabel')}
          </label>
          <div className="relative">
            <Search size={18} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={searchText}
              onChange={(e) => setSearchText(e.target.value)}
              placeholder={t('outputs.searchPlaceholder')}
              className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
            />
          </div>
        </div>
        <div>
          <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
            {t('outputs.typeLabel')}
          </label>
          <select
            value={type}
            onChange={(e) => setType(e.target.value as OutputTypeValue)}
            className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
          >
            <option value="">{t('outputs.types.all')}</option>
            <option value="Recording">{t('outputs.types.recording')}</option>
            <option value="Summary">{t('outputs.types.summary')}</option>
          </select>
        </div>
        <div>
          <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
            {t('outputs.statusLabel')}
          </label>
          <select
            value={status}
            onChange={(e) => setStatus(e.target.value as OutputStatusValue)}
            className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
          >
            <option value="">{t('outputs.statuses.all')}</option>
            <option value="Available">{t('outputs.statuses.available')}</option>
            <option value="Processing">{t('outputs.statuses.processing')}</option>
            <option value="Generating">{t('outputs.statuses.generating')}</option>
            <option value="Failed">{t('outputs.statuses.failed')}</option>
          </select>
        </div>
      </div>

      {outputsQuery.isLoading && !data ? (
        <div className="p-8 text-slate-500">{t('outputs.loading')}</div>
      ) : outputsQuery.isError ? (
        <div className="p-8 text-red-500">{t('outputs.loadError')}</div>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
            <table className="w-full table-fixed text-left text-sm">
              <colgroup>
                <col style={{ width: '13%' }} />
                <col style={{ width: '25%' }} />
                <col style={{ width: '20%' }} />
                <col style={{ width: '13%' }} />
                <col style={{ width: '10%' }} />
                <col style={{ width: '11%' }} />
                <col style={{ width: '8%' }} />
              </colgroup>
              <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
                <tr>
                  <th className="p-4 text-center">{t('outputs.table.type')}</th>
                  <th className="p-4 text-center">{t('outputs.table.session')}</th>
                  <th className="p-4 text-center">{t('outputs.table.classroom')}</th>
                  <th className="p-4 text-center">{t('outputs.table.status')}</th>
                  <th className="p-4 text-center">{t('outputs.table.size')}</th>
                  <th className="p-4 text-center">{t('outputs.table.created')}</th>
                  <th className="p-4 text-center">{t('outputs.table.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {outputs.length === 0 ? (
                  <tr>
                    <td colSpan={7} className="p-8 text-center text-gray-500">
                      {t('outputs.empty')}
                    </td>
                  </tr>
                ) : (
                  outputs.map((o: OutputItem) => (
                    <tr key={`${o.type}-${o.outputId}`} className="border-b dark:border-gray-800">
                      <td className="p-4">
                        <div className="flex items-center justify-center gap-1.5 text-slate-600 dark:text-slate-300">
                          {o.type === 'Summary' ? <ScrollText size={15} /> : <FileVideo size={15} />}
                          <span className="text-xs">
                            {o.type === 'Summary' ? t('outputs.types.summary') : t('outputs.types.recording')}
                          </span>
                        </div>
                      </td>
                      <td className="p-4 font-medium dark:text-gray-200">
                        <div className="truncate">{o.sessionTitle || '—'}</div>
                      </td>
                      <td className="p-4 dark:text-gray-300">
                        <div className="truncate">{o.className || '—'}</div>
                      </td>
                      <td className="p-4">
                        <div className="flex justify-center">
                          <span className={`inline-flex min-w-[72px] items-center justify-center rounded-full px-2 py-1 text-xs font-medium ${statusBadge(o.status)}`}>
                            {statusLabel(o.status)}
                          </span>
                        </div>
                      </td>
                      <td className="p-4 text-center text-xs dark:text-gray-400">{formatBytes(o.sizeBytes)}</td>
                      <td className="p-4 text-center text-xs dark:text-gray-400">
                        {new Date(o.createdAtUtc).toLocaleDateString()}
                      </td>
                      <td className="p-4">
                        <div className="flex justify-center">
                          <button
                            type="button"
                            title={t('outputs.table.delete')}
                            aria-label={t('outputs.table.delete')}
                            onClick={() => setDeleteTarget(o)}
                            className="inline-flex items-center justify-center rounded-md border border-red-200 p-1.5 text-red-600 transition-colors hover:bg-red-50 dark:border-red-900/50 dark:text-red-400 dark:hover:bg-red-950/30"
                          >
                            <Trash2 size={14} />
                          </button>
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
              <span>{t('outputs.pagination.showing', { count: outputs.length, total: data.totalCount })}</span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={page <= 1 || outputsQuery.isFetching}
                  className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
                >
                  <ChevronLeft size={16} />
                  {t('users.pagination.previous')}
                </button>
                <span className="px-2">{t('users.pagination.page', { page, total: totalPages })}</span>
                <button
                  type="button"
                  onClick={() => setPage((p) => p + 1)}
                  disabled={page >= totalPages || outputsQuery.isFetching}
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

      <DeleteOutputDialog output={deleteTarget} onClose={() => setDeleteTarget(null)} />
    </div>
  );
};
