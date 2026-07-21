import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';
import { useFileDetail } from '../hooks/useKnowledgeQueries';

interface FileDetailDialogProps {
  fileId: string | null;
  onClose: () => void;
}

const formatBytes = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / Math.pow(1024, exponent)).toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
};

/** Step 4: a file's indexing diagnostics — status, attempts and the failure reason. */
export const FileDetailDialog = ({ fileId, onClose }: FileDetailDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const query = useFileDetail(fileId);

  if (!fileId) return null;
  const detail = query.data;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="mb-4 flex items-start justify-between gap-3">
          <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
            {t('knowledge.detail.title')}
          </h2>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1 text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800"
          >
            <X size={18} />
          </button>
        </div>

        {query.isLoading ? (
          <div className="p-6 text-sm text-slate-500">{t('knowledge.detail.loading')}</div>
        ) : query.isError || !detail ? (
          <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {t('knowledge.detail.loadError')}
          </div>
        ) : (
          <dl className="space-y-3 text-sm">
            <Row label={t('knowledge.detail.fileName')} value={detail.fileName} />
            <Row label={t('knowledge.detail.classroom')} value={detail.className ?? detail.classroomId} />
            <Row label={t('knowledge.detail.size')} value={formatBytes(detail.sizeBytes)} />
            <Row label={t('knowledge.detail.status')} value={detail.status} />
            <Row label={t('knowledge.detail.attempts')} value={String(detail.attempts)} />
            <Row label={t('knowledge.detail.chunks')} value={String(detail.chunkCount)} />
            {detail.lastError && (
              <div>
                <dt className="mb-1 font-medium text-slate-500 dark:text-slate-400">
                  {t('knowledge.detail.lastError')}
                </dt>
                <dd className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 font-mono text-xs text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
                  {detail.lastError}
                </dd>
              </div>
            )}
          </dl>
        )}
      </div>
    </div>
  );
};

const Row = ({ label, value }: { label: string; value: string }) => (
  <div className="flex items-center justify-between gap-4">
    <dt className="text-slate-500 dark:text-slate-400">{label}</dt>
    <dd className="truncate font-medium text-slate-800 dark:text-slate-100">{value}</dd>
  </div>
);
