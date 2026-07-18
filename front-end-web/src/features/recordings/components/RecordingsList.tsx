import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Clock, HardDrive, Loader2, RefreshCw, Video } from 'lucide-react';
import { ArtifactList } from '../../../components/ui/ArtifactList';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import { SecureDownloadButton } from '../../../components/ui/SecureDownloadButton';
import { formatBytes, formatDuration } from '../../../utils/format';
import { downloadRecording } from '../api/recordings';
import { useClassroomRecordings } from '../hooks/useRecordingQueries';
import type { Recording } from '../types';

interface RecordingsListProps {
  classroomId: string;
  /** When provided, only recordings for this session are listed. */
  sessionId?: string;
}

export const RecordingsList = ({
  classroomId,
  sessionId,
}: RecordingsListProps) => {
  const { t, i18n } = useTranslation('recordings');

  const {
    data: recordings = [],
    isLoading,
    isError,
    refetch,
    isFetching,
  } = useClassroomRecordings(classroomId, sessionId);

  // Newest-first.
  const sortedRecordings = useMemo(
    () =>
      [...recordings].sort(
        (a, b) =>
          new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
      ),
    [recordings],
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
        isEmpty={sortedRecordings.length === 0}
        emptyIcon={<Video className="mx-auto" size={48} aria-hidden="true" />}
        emptyTitle={t('empty.title')}
        emptyDescription={t('empty.description')}
        errorTitle={t('error.title')}
        errorDescription={t('error.description')}
        onRetry={() => refetch()}
      >
        {sortedRecordings.map((recording) => (
          <RecordingRow
            key={recording.recordingId}
            recording={recording}
            classroomId={classroomId}
            dateLabel={formatDate(recording.createdAt)}
          />
        ))}
      </ArtifactList>
    </div>
  );
};

interface RecordingRowProps {
  recording: Recording;
  classroomId: string;
  dateLabel: string;
}

const RecordingRow = ({
  recording,
  classroomId,
  dateLabel,
}: RecordingRowProps) => {
  const { t } = useTranslation('recordings');

  return (
    <div className="flex flex-col gap-4 rounded-2xl border border-slate-200 bg-white p-5 transition-all dark:border-slate-800 dark:bg-slate-900/50 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex items-start gap-4">
        <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-slate-100 text-slate-400 dark:bg-slate-800">
          <Video size={24} aria-hidden="true" />
        </div>
        <div>
          <div className="flex items-center gap-3">
            <h4 className="font-bold text-slate-900 dark:text-white">
              {t('recordedOn', { date: dateLabel })}
            </h4>
            <StatusBadge status={recording.status} />
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-3 text-[10px] font-bold uppercase tracking-wider text-slate-400">
            <span className="flex items-center gap-1.5">
              <Clock size={12} aria-hidden="true" />
              {formatDuration(recording.durationSeconds)}
            </span>
            <span className="flex items-center gap-1.5">
              <HardDrive size={12} aria-hidden="true" />
              {formatBytes(recording.sizeBytes)}
            </span>
          </div>
        </div>
      </div>

      <div className="flex items-center gap-3 ltr:sm:ml-auto rtl:sm:mr-auto">
        {recording.status === 'Available' && (
          <SecureDownloadButton
            onDownload={() =>
              downloadRecording(classroomId, recording.recordingId)
            }
            ariaLabel={t('downloadAria', { date: dateLabel })}
          />
        )}

        {recording.status === 'Processing' && (
          <span
            className="inline-flex items-center gap-2 text-sm font-medium text-amber-600 dark:text-amber-400"
            role="status"
          >
            <Loader2 size={16} className="animate-spin" aria-hidden="true" />
            {t('processing')}
          </span>
        )}

        {recording.status === 'Failed' && (
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
