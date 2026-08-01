import type { ReactElement } from 'react';
import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  Database,
  FileText,
  Info,
  Layers,
  RefreshCw,
  Search,
  Trash2,
} from 'lucide-react';
import { useToast } from '../../../components/ui/ToastProvider';
import {
  useKnowledgeFiles,
  useKnowledgeStats,
  useReindexFile,
  useReindexClassroom,
  useDeleteFile,
} from '../hooks/useKnowledgeQueries';
import { KnowledgeActionDialog } from './KnowledgeActionDialog';
import { FileDetailDialog } from './FileDetailDialog';
import type { AdminFileItem, IndexingStatusValue } from '../types';

const PAGE_SIZE = 20;

const formatBytes = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / Math.pow(1024, exponent)).toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
};

const statusBadge = (status?: string | null): string => {
  switch (status) {
    case 'Done':
      return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
    case 'Failed':
      return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
    case 'Processing':
      return 'bg-amber-100 text-amber-800 dark:bg-amber-950/40 dark:text-amber-300';
    case 'Pending':
      return 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400';
    default:
      return 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400';
  }
};

type ReindexFileTarget = { kind: 'file'; id: string; name: string };
type ReindexClassroomTarget = { kind: 'classroom'; id: string; name: string };
type DeleteTarget = { id: string; name: string };

export const KnowledgeBasePage = () => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();

  const [searchText, setSearchText] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [status, setStatus] = useState<IndexingStatusValue>('');
  const [page, setPage] = useState(1);

  const [detailFileId, setDetailFileId] = useState<string | null>(null);
  const [reindexTarget, setReindexTarget] = useState<ReindexFileTarget | ReindexClassroomTarget | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null);

  useEffect(() => {
    const id = window.setTimeout(() => setDebouncedSearch(searchText.trim()), 300);
    return () => window.clearTimeout(id);
  }, [searchText]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, status]);

  const filesQuery = useKnowledgeFiles({
    search: debouncedSearch,
    status,
    page,
    pageSize: PAGE_SIZE,
  });
  const statsQuery = useKnowledgeStats(undefined, true);

  const reindexFile = useReindexFile();
  const reindexClassroom = useReindexClassroom();
  const deleteFile = useDeleteFile();

  const data = filesQuery.data;
  const files = data?.items ?? [];
  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1;

  const stats = statsQuery.data;
  const statusOrder = ['Done', 'Processing', 'Pending', 'Failed'];
  const statusChips = useMemo(() => {
    if (!stats) return [];
    return statusOrder
      .filter((s) => stats.statusCounts[s] !== undefined)
      .map((s) => ({ status: s, count: stats.statusCounts[s] }));
  }, [stats]);

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
        <h1 className="text-3xl font-bold dark:text-white">{t('knowledge.title')}</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{t('knowledge.subtitle')}</p>
      </div>

      {/* Step 5: knowledge-base statistics (platform-wide). */}
      {stats && (
        <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
          <StatCard icon={<FileText size={16} />} label={t('knowledge.stats.documents')} value={stats.documentCount} />
          <StatCard icon={<Layers size={16} />} label={t('knowledge.stats.chunks')} value={stats.totalChunks} />
          <StatCard icon={<RefreshCw size={16} />} label={t('knowledge.stats.failed')} value={stats.failedCount} tone={stats.failedCount > 0 ? 'warn' : undefined} />
          <StatCard icon={<Database size={16} />} label={t('knowledge.stats.storage')} value={formatBytes(stats.storageBytes)} />
        </div>
      )}
      {statusChips.length > 0 && (
        <div className="mb-6 flex flex-wrap gap-2">
          {statusChips.map((c) => (
            <span key={c.status} className={`rounded-full px-3 py-1 text-xs font-medium ${statusBadge(c.status)}`}>
              {c.status}: {c.count}
            </span>
          ))}
        </div>
      )}

      {/* Filters */}
      <div className="mb-6 flex flex-wrap items-end gap-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="min-w-[240px] flex-1">
          <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
            {t('knowledge.searchLabel')}
          </label>
          <div className="relative">
            <Search size={18} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={searchText}
              onChange={(e) => setSearchText(e.target.value)}
              placeholder={t('knowledge.searchPlaceholder')}
              className="w-full rounded-lg border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none placeholder:text-slate-400 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
            />
          </div>
        </div>
        <div>
          <label className="mb-2 block text-sm font-medium text-slate-700 dark:text-slate-300">
            {t('knowledge.statusLabel')}
          </label>
          <select
            value={status}
            onChange={(e) => setStatus(e.target.value as IndexingStatusValue)}
            className="rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-100"
          >
            <option value="">{t('knowledge.statuses.all')}</option>
            <option value="Pending">{t('knowledge.statuses.pending')}</option>
            <option value="Processing">{t('knowledge.statuses.processing')}</option>
            <option value="Done">{t('knowledge.statuses.done')}</option>
            <option value="Failed">{t('knowledge.statuses.failed')}</option>
          </select>
        </div>
      </div>

      {/* Alt path 3أ: indexing status could not be fetched. */}
      {data?.indexingUnavailable && (
        <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
          {t('knowledge.indexingUnavailable')}
        </div>
      )}

      {filesQuery.isLoading && !data ? (
        <div className="p-8 text-slate-500">{t('knowledge.loading')}</div>
      ) : filesQuery.isError ? (
        <div className="p-8 text-red-500">{t('knowledge.loadError')}</div>
      ) : (
        <>
          <div className="overflow-hidden rounded-lg border bg-white shadow dark:border-gray-800 dark:bg-gray-900">
            <table className="w-full table-fixed text-left text-sm">
              <colgroup>
                <col style={{ width: '30%' }} />
                <col style={{ width: '20%' }} />
                <col style={{ width: '12%' }} />
                <col style={{ width: '14%' }} />
                <col style={{ width: '10%' }} />
                <col style={{ width: '14%' }} />
              </colgroup>
              <thead className="border-b bg-gray-50 dark:border-gray-700 dark:bg-gray-800">
                <tr>
                  <th className="p-4 text-center">{t('knowledge.table.file')}</th>
                  <th className="p-4 text-center">{t('knowledge.table.classroom')}</th>
                  <th className="p-4 text-center">{t('knowledge.table.size')}</th>
                  <th className="p-4 text-center">{t('knowledge.table.status')}</th>
                  <th className="p-4 text-center">{t('knowledge.table.chunks')}</th>
                  <th className="p-4 text-center">{t('knowledge.table.actions')}</th>
                </tr>
              </thead>
              <tbody>
                {files.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="p-8 text-center text-gray-500">
                      {t('knowledge.empty')}
                    </td>
                  </tr>
                ) : (
                  files.map((f: AdminFileItem) => (
                    <tr key={f.fileId} className="border-b dark:border-gray-800">
                      <td className="p-4 font-medium dark:text-gray-200">
                        <div className="truncate">{f.fileName}</div>
                        <div className="truncate text-xs text-gray-500">{f.contentType}</div>
                      </td>
                      <td className="p-4 dark:text-gray-300">
                        <div className="truncate">{f.className ?? t('knowledge.table.unknownClassroom')}</div>
                      </td>
                      <td className="p-4 text-center text-xs dark:text-gray-400">{formatBytes(f.sizeBytes)}</td>
                      <td className="p-4">
                        <div className="flex justify-center">
                          <span className={`inline-flex min-w-[72px] items-center justify-center rounded-full px-2 py-1 text-xs font-medium ${statusBadge(f.status)}`}>
                            {f.status ?? t('knowledge.table.statusUnknown')}
                          </span>
                        </div>
                      </td>
                      <td className="p-4 text-center dark:text-gray-300">
                        {f.chunkCount ?? '—'}
                      </td>
                      <td className="p-4">
                        <div className="flex justify-center gap-1.5">
                          <IconButton title={t('knowledge.table.detail')} onClick={() => setDetailFileId(f.fileId)}>
                            <Info size={14} />
                          </IconButton>
                          <IconButton
                            title={t('knowledge.table.reindexFile')}
                            onClick={() => setReindexTarget({ kind: 'file', id: f.fileId, name: f.fileName })}
                          >
                            <RefreshCw size={14} />
                          </IconButton>
                          <IconButton
                            title={t('knowledge.table.reindexClassroom')}
                            onClick={() =>
                              setReindexTarget({
                                kind: 'classroom',
                                id: f.classroomId,
                                name: f.className ?? f.classroomId,
                              })
                            }
                          >
                            <Layers size={14} />
                          </IconButton>
                          <IconButton
                            title={t('knowledge.table.delete')}
                            danger
                            onClick={() => setDeleteTarget({ id: f.fileId, name: f.fileName })}
                          >
                            <Trash2 size={14} />
                          </IconButton>
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
              <span>{t('knowledge.pagination.showing', { count: files.length, total: data.totalCount })}</span>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={page <= 1 || filesQuery.isFetching}
                  className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 font-medium transition-colors hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-800 dark:hover:bg-slate-900"
                >
                  <ChevronLeft size={16} />
                  {t('users.pagination.previous')}
                </button>
                <span className="px-2">{t('users.pagination.page', { page, total: totalPages })}</span>
                <button
                  type="button"
                  onClick={() => setPage((p) => p + 1)}
                  disabled={page >= totalPages || filesQuery.isFetching}
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

      <FileDetailDialog fileId={detailFileId} onClose={() => setDetailFileId(null)} />

      {/* Reindex (file or classroom) */}
      <KnowledgeActionDialog
        open={reindexTarget !== null}
        variant="reindex"
        title={
          reindexTarget?.kind === 'classroom'
            ? t('knowledge.actions.reindexClassroomTitle')
            : t('knowledge.actions.reindexFileTitle')
        }
        description={
          reindexTarget?.kind === 'classroom'
            ? t('knowledge.actions.reindexClassroomDescription', { name: reindexTarget?.name ?? '' })
            : t('knowledge.actions.reindexFileDescription', { name: reindexTarget?.name ?? '' })
        }
        confirmLabel={t('knowledge.actions.reindexConfirm')}
        pendingLabel={t('knowledge.actions.reindexing')}
        showFailedOnly={reindexTarget?.kind === 'classroom'}
        onConfirm={async (reason, failedOnly) => {
          if (!reindexTarget) return;
          if (reindexTarget.kind === 'file') {
            await reindexFile.mutateAsync({ fileId: reindexTarget.id, reason });
            showToast({ type: 'success', title: t('knowledge.actions.reindexQueuedTitle'), message: t('knowledge.actions.reindexFileQueued') });
          } else {
            const result = await reindexClassroom.mutateAsync({
              classroomId: reindexTarget.id,
              failedOnly,
              reason,
            });
            showToast({
              type: result.skipped > 0 ? 'warning' : 'success',
              title: t('knowledge.actions.reindexQueuedTitle'),
              message: t('knowledge.actions.reindexClassroomQueued', {
                enqueued: result.enqueued,
                skipped: result.skipped,
              }),
            });
          }
        }}
        onClose={() => setReindexTarget(null)}
      />

      {/* Delete file */}
      <KnowledgeActionDialog
        open={deleteTarget !== null}
        variant="delete"
        title={t('knowledge.actions.deleteTitle')}
        description={t('knowledge.actions.deleteDescription', { name: deleteTarget?.name ?? '' })}
        confirmLabel={t('knowledge.actions.deleteConfirm')}
        pendingLabel={t('knowledge.actions.deleting')}
        onConfirm={async (reason) => {
          if (!deleteTarget) return;
          await deleteFile.mutateAsync({ fileId: deleteTarget.id, reason });
          showToast({ type: 'success', title: t('knowledge.actions.deletedTitle'), message: t('knowledge.actions.deleted', { name: deleteTarget.name }) });
        }}
        onClose={() => setDeleteTarget(null)}
      />
    </div>
  );
};

const StatCard = ({
  icon,
  label,
  value,
  tone,
}: {
  icon: ReactElement;
  label: string;
  value: number | string;
  tone?: 'warn';
}) => (
  <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
    <div className="mb-1 flex items-center gap-2 text-slate-400">
      {icon}
      <span className="text-xs font-medium uppercase tracking-wide">{label}</span>
    </div>
    <div className={`text-2xl font-bold ${tone === 'warn' ? 'text-amber-600 dark:text-amber-400' : 'dark:text-white'}`}>
      {value}
    </div>
  </div>
);

const IconButton = ({
  title,
  onClick,
  danger,
  children,
}: {
  title: string;
  onClick: () => void;
  danger?: boolean;
  children: React.ReactNode;
}) => (
  <button
    type="button"
    title={title}
    aria-label={title}
    onClick={onClick}
    className={`inline-flex items-center justify-center rounded-md border p-1.5 transition-colors ${
      danger
        ? 'border-red-200 text-red-600 hover:bg-red-50 dark:border-red-900/50 dark:text-red-400 dark:hover:bg-red-950/30'
        : 'border-slate-200 text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800'
    }`}
  >
    {children}
  </button>
);
