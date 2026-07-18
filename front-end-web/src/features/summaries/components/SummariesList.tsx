import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Clock, Eye, FileText, Loader2, RefreshCw } from 'lucide-react';
import { ArtifactList } from '../../../components/ui/ArtifactList';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import { SecureDownloadButton } from '../../../components/ui/SecureDownloadButton';
import { downloadSummary } from '../api/summaries';
import { useClassroomSummaries } from '../hooks/useSummaryQueries';
import type { Summary } from '../types';
import { SummaryPreview } from './SummaryPreview';

interface SummariesListProps {
  classroomId: string;
  /** When provided, only summaries for this session are listed. */
  sessionId?: string;
}

export const SummariesList = ({ classroomId, sessionId }: SummariesListProps) => {
  const { t, i18n } = useTranslation('summaries');
  const [preview, setPreview] = useState<{ id: string; label: string } | null>(
    null,
  );

  const {
    data: summaries = [],
    isLoading,
    isError,
    refetch,
    isFetching,
  } = useClassroomSummaries(classroomId, sessionId);

  // Newest-first.
  const sortedSummaries = useMemo(
    () =>
      [...summaries].sort(
        (a, b) =>
          new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
      ),
    [summaries],
  );

  const formatDate = (iso: string) =>
    new Date(iso).toLocaleString(i18n.language, {
      dateStyle: 'medium',
      timeStyle: 'short',
    });

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500 dark:text-slate-400">
          {t('description')}
        </p>
        <button
          type="button"
          onClick={() => refetch()}
          disabled={isFetching}
          className="inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium text-slate-600 transition-colors hover:bg-slate-100 disabled:opacity-50 dark:text-slate-300 dark:hover:bg-slate-800"
          aria-label={t('refresh', { ns: 'common' })}
        >
          <RefreshCw
            size={16}
            className={isFetching ? 'animate-spin' : ''}
            aria-hidden="true"
          />
          {t('refresh', { ns: 'common' })}
        </button>
      </div>

      <ArtifactList
        isLoading={isLoading}
        isError={isError}
        isEmpty={sortedSummaries.length === 0}
        emptyIcon={<FileText className="mx-auto" size={48} aria-hidden="true" />}
        emptyTitle={t('empty.title')}
        emptyDescription={t('empty.description')}
        errorTitle={t('error.title')}
        errorDescription={t('error.description')}
        onRetry={() => refetch()}
      >
        {sortedSummaries.map((summary) => (
          <SummaryRow
            key={summary.summaryId}
            summary={summary}
            classroomId={classroomId}
            dateLabel={formatDate(summary.createdAt)}
            onPreview={(label) => setPreview({ id: summary.summaryId, label })}
          />
        ))}
      </ArtifactList>

      {preview && (
        <SummaryPreview
          isOpen
          onClose={() => setPreview(null)}
          classroomId={classroomId}
          summaryId={preview.id}
          label={preview.label}
        />
      )}
    </div>
  );
};

interface SummaryRowProps {
  summary: Summary;
  classroomId: string;
  dateLabel: string;
  onPreview: (label: string) => void;
}

const SummaryRow = ({
  summary,
  classroomId,
  dateLabel,
  onPreview,
}: SummaryRowProps) => {
  const { t } = useTranslation('summaries');
  const title = t('createdOn', { date: dateLabel });

  return (
    <div className="flex flex-col gap-4 rounded-2xl border border-slate-200 bg-white p-5 transition-all dark:border-slate-800 dark:bg-slate-900/50 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex items-start gap-4">
        <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-slate-100 text-slate-400 dark:bg-slate-800">
          <FileText size={24} aria-hidden="true" />
        </div>
        <div>
          <div className="flex items-center gap-3">
            <h4 className="font-bold text-slate-900 dark:text-white">{title}</h4>
            <StatusBadge status={summary.status} />
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-3 text-[10px] font-bold uppercase tracking-wider text-slate-400">
            <span className="flex items-center gap-1.5">
              <Clock size={12} aria-hidden="true" />
              {dateLabel}
            </span>
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-3 ltr:sm:ml-auto rtl:sm:mr-auto">
        {summary.status === 'Available' && (
          <>
            <button
              type="button"
              onClick={() => onPreview(title)}
              aria-label={t('preview.openAria', { date: dateLabel })}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 active:scale-[0.98] dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200 dark:hover:bg-slate-900"
            >
              <Eye size={16} aria-hidden="true" />
              {t('preview.open')}
            </button>

            <SecureDownloadButton
              onDownload={() =>
                downloadSummary(classroomId, summary.summaryId, 'pdf')
              }
              label={t('downloadPdf')}
              ariaLabel={t('downloadPdfAria', { date: dateLabel })}
            />

            <SecureDownloadButton
              variant="secondary"
              onDownload={() =>
                downloadSummary(classroomId, summary.summaryId, 'md')
              }
              label={t('downloadMd')}
              ariaLabel={t('downloadMdAria', { date: dateLabel })}
            />
          </>
        )}

        {summary.status === 'Generating' && (
          <span
            className="inline-flex items-center gap-2 text-sm font-medium text-amber-600 dark:text-amber-400"
            role="status"
          >
            <Loader2 size={16} className="animate-spin" aria-hidden="true" />
            {t('generating')}
          </span>
        )}

        {summary.status === 'Failed' && (
          <span
            className="text-sm font-medium text-red-600 dark:text-red-400"
            role="alert"
          >
            {t('failedRow')}
          </span>
        )}
      </div>
    </div>
  );
};
