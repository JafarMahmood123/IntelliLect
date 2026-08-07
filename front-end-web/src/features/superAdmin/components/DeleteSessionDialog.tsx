import type { ReactElement } from 'react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, HardDrive, Radio, ScrollText, Check, X } from 'lucide-react';
import { useToast } from '../../../components/ui/ToastProvider';
import {
  useSessionDeletionImpact,
  useDeleteSession,
} from '../hooks/useSessionQueries';

interface DeleteSessionDialogProps {
  sessionId: string | null;
  sessionTitle: string;
  onClose: () => void;
  onDeleted: (title: string) => void;
}

/** Human-readable bytes, e.g. 1536 -> "1.5 KB". */
const formatBytes = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
};

export const DeleteSessionDialog = ({
  sessionId,
  sessionTitle,
  onClose,
  onDeleted,
}: DeleteSessionDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');

  const impactQuery = useSessionDeletionImpact(sessionId);
  const mutation = useDeleteSession();

  useEffect(() => {
    if (sessionId) {
      setReason('');
      setError('');
    }
  }, [sessionId]);

  if (!sessionId) return null;

  const impact = impactQuery.data;
  // Precondition 5ب: cannot delete while the session is live.
  const blockedByLive = impact?.isLive ?? false;

  const submit = async () => {
    // Alternate path 4أ: the reason (and thereby the confirmation) is required.
    if (reason.trim().length === 0) {
      setError(t('sessions.delete.reasonRequired'));
      return;
    }
    if (blockedByLive) {
      setError(t('sessions.delete.liveBlock'));
      return;
    }
    setError('');

    try {
      await mutation.mutateAsync({ sessionId, reason: reason.trim() });
      // Step 8: confirm success.
      showToast({
        type: 'success',
        title: t('sessions.delete.successTitle'),
        message: t('sessions.delete.success', { title: sessionTitle }),
      });
      onDeleted(sessionTitle);
      onClose();
    } catch (err: any) {
      const status = err?.response?.status;
      if (status === 404) {
        setError(t('sessions.delete.notFound')); // 5أ
      } else if (status === 409) {
        setError(t('sessions.delete.liveBlock')); // 5ب
      } else {
        setError(err?.response?.data?.detail || t('sessions.delete.fallbackError'));
      }
    }
  };

  // Each output line shows present/absent so the admin sees exactly what is lost (step 3, 6أ).
  const OutputRow = ({
    icon,
    label,
    present,
    note,
  }: {
    icon: ReactElement;
    label: string;
    present: boolean;
    note?: string;
  }) => (
    <div className="flex items-center gap-2 text-sm">
      <span className="text-slate-400">{icon}</span>
      <span className="text-slate-700 dark:text-slate-200">{label}</span>
      <span className="ms-auto inline-flex items-center gap-1">
        {note ? (
          <span className="text-xs text-amber-600 dark:text-amber-400">{note}</span>
        ) : present ? (
          <Check size={16} className="text-emerald-600 dark:text-emerald-400" />
        ) : (
          <X size={16} className="text-slate-300 dark:text-slate-600" />
        )}
      </span>
    </div>
  );

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="mb-4 flex items-start gap-3">
          <span className="rounded-lg bg-red-100 p-2 text-red-600 dark:bg-red-950/40 dark:text-red-400">
            <AlertTriangle size={20} />
          </span>
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
              {t('sessions.delete.title')}
            </h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('sessions.delete.description', { title: sessionTitle })}
            </p>
          </div>
        </div>

        {/* Step 3: impact preview. */}
        {impactQuery.isLoading ? (
          <div className="mb-4 rounded-lg border border-slate-200 p-4 text-sm text-slate-500 dark:border-slate-800">
            {t('sessions.delete.loadingImpact')}
          </div>
        ) : impactQuery.isError ? (
          <div className="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {t('sessions.delete.impactError')}
          </div>
        ) : (
          impact && (
            <>
              <p className="mb-2 text-xs font-medium uppercase tracking-wide text-slate-400">
                {t('sessions.delete.impactTitle')}
              </p>
              <div className="mb-4 space-y-2 rounded-lg border border-slate-200 p-3 dark:border-slate-800">
                <OutputRow
                  icon={<Radio size={16} />}
                  label={t('sessions.delete.outputs.recording')}
                  present={impact.hasRecording}
                />
                <OutputRow
                  icon={<ScrollText size={16} />}
                  label={t('sessions.delete.outputs.summary')}
                  present={impact.hasSummary}
                />
                <OutputRow
                  icon={<ScrollText size={16} />}
                  label={t('sessions.delete.outputs.transcript')}
                  present={impact.hasTranscript}
                  note={impact.transcriptUnavailable ? t('sessions.delete.transcriptUnavailable') : undefined}
                />
                <div className="flex items-center gap-2 border-t border-slate-100 pt-2 text-sm dark:border-slate-800">
                  <span className="text-slate-400"><HardDrive size={16} /></span>
                  <span className="text-slate-700 dark:text-slate-200">
                    {t('sessions.delete.outputs.storage')}
                  </span>
                  <span className="ms-auto font-semibold text-slate-800 dark:text-slate-100">
                    {formatBytes(impact.storageBytes)}
                  </span>
                </div>
              </div>
            </>
          )
        )}

        {/* Precondition 5ب warning. */}
        {blockedByLive && (
          <div className="mb-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/50 dark:bg-amber-950/30 dark:text-amber-300">
            {t('sessions.delete.liveBlock')}
          </div>
        )}

        {error && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {error}
          </div>
        )}

        <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('sessions.delete.reasonLabel')}
        </label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          placeholder={t('sessions.delete.reasonPlaceholder')}
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
            disabled={mutation.isPending || blockedByLive}
            className="inline-flex items-center justify-center rounded-lg bg-red-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {mutation.isPending ? t('sessions.delete.deleting') : t('sessions.delete.confirm')}
          </button>
        </div>
      </div>
    </div>
  );
};
