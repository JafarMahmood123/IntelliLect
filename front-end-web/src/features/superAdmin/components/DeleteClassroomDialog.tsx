import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  FileText,
  HardDrive,
  Presentation,
  Radio,
  ScrollText,
  Users as UsersIcon,
} from 'lucide-react';
import { useToast } from '../../../components/ui/ToastProvider';
import {
  useClassroomDeletionImpact,
  useDeleteClassroom,
} from '../hooks/useClassroomQueries';

interface DeleteClassroomDialogProps {
  classroomId: string | null;
  classroomName: string;
  onClose: () => void;
  onDeleted: (name: string) => void;
}

/** Human-readable bytes, e.g. 1536 -> "1.5 KB". */
const formatBytes = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
};

export const DeleteClassroomDialog = ({
  classroomId,
  classroomName,
  onClose,
  onDeleted,
}: DeleteClassroomDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');

  const impactQuery = useClassroomDeletionImpact(classroomId);
  const mutation = useDeleteClassroom();

  useEffect(() => {
    if (classroomId) {
      setReason('');
      setError('');
    }
  }, [classroomId]);

  if (!classroomId) return null;

  const impact = impactQuery.data;
  // Precondition 5ب: cannot delete while a session is live.
  const blockedByLiveSession = impact?.hasLiveSession ?? false;

  const submit = async () => {
    // Alternate path 4أ: the reason (and thereby the confirmation) is required.
    if (reason.trim().length === 0) {
      setError(t('classrooms.delete.reasonRequired'));
      return;
    }
    if (blockedByLiveSession) {
      setError(t('classrooms.delete.liveSessionBlock'));
      return;
    }
    setError('');

    try {
      const result = await mutation.mutateAsync({ id: classroomId, reason: reason.trim() });
      // Step 8: confirm success with the counts of what was removed.
      showToast({
        type: 'success',
        title: t('classrooms.delete.successTitle'),
        message: t('classrooms.delete.success', {
          name: classroomName,
          sessions: result.sessionsDeleted,
          files: result.filesDeleted,
        }),
      });
      onDeleted(classroomName);
      onClose();
    } catch (err: any) {
      const status = err?.response?.status;
      if (status === 404) {
        setError(t('classrooms.delete.notFound')); // 5أ
      } else if (status === 409) {
        setError(t('classrooms.delete.liveSessionBlock')); // 5ب
      } else {
        setError(err?.response?.data?.detail || t('classrooms.delete.fallbackError'));
      }
    }
  };

  const stats: Array<{ icon: JSX.Element; label: string; value: string | number }> = impact
    ? [
        { icon: <Presentation size={16} />, label: t('classrooms.delete.stats.sessions'), value: impact.sessionCount },
        { icon: <UsersIcon size={16} />, label: t('classrooms.delete.stats.members'), value: impact.memberCount },
        { icon: <FileText size={16} />, label: t('classrooms.delete.stats.files'), value: impact.fileCount },
        { icon: <Radio size={16} />, label: t('classrooms.delete.stats.recordings'), value: impact.recordingCount },
        { icon: <ScrollText size={16} />, label: t('classrooms.delete.stats.summaries'), value: impact.summaryCount },
        { icon: <HardDrive size={16} />, label: t('classrooms.delete.stats.storage'), value: formatBytes(impact.storageBytes) },
      ]
    : [];

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="mb-4 flex items-start gap-3">
          <span className="rounded-lg bg-red-100 p-2 text-red-600 dark:bg-red-950/40 dark:text-red-400">
            <AlertTriangle size={20} />
          </span>
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
              {t('classrooms.delete.title')}
            </h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('classrooms.delete.description', { name: classroomName })}
            </p>
          </div>
        </div>

        {/* Step 3: impact preview. */}
        {impactQuery.isLoading ? (
          <div className="mb-4 rounded-lg border border-slate-200 p-4 text-sm text-slate-500 dark:border-slate-800">
            {t('classrooms.delete.loadingImpact')}
          </div>
        ) : impactQuery.isError ? (
          <div className="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {t('classrooms.delete.impactError')}
          </div>
        ) : (
          impact && (
            <>
              <p className="mb-2 text-xs font-medium uppercase tracking-wide text-slate-400">
                {t('classrooms.delete.impactTitle')}
              </p>
              <div className="mb-4 grid grid-cols-2 gap-2 rounded-lg border border-slate-200 p-3 dark:border-slate-800 sm:grid-cols-3">
                {stats.map((s) => (
                  <div key={s.label} className="flex items-center gap-2 text-sm">
                    <span className="text-slate-400">{s.icon}</span>
                    <span className="font-semibold text-slate-800 dark:text-slate-100">{s.value}</span>
                    <span className="text-slate-500 dark:text-slate-400">{s.label}</span>
                  </div>
                ))}
              </div>
            </>
          )
        )}

        {/* Precondition 5ب warning. */}
        {blockedByLiveSession && (
          <div className="mb-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
            {t('classrooms.delete.liveSessionBlock')}
          </div>
        )}

        {error && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {error}
          </div>
        )}

        <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('classrooms.delete.reasonLabel')}
        </label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          placeholder={t('classrooms.delete.reasonPlaceholder')}
          className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950/50 dark:text-slate-100"
        />

        <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={mutation.isPending}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
          >
            {t('common:buttons.cancel')}
          </button>
          <button
            type="button"
            onClick={submit}
            disabled={mutation.isPending || blockedByLiveSession}
            className="inline-flex items-center justify-center rounded-lg bg-red-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {mutation.isPending ? t('classrooms.delete.deleting') : t('classrooms.delete.confirm')}
          </button>
        </div>
      </div>
    </div>
  );
};
