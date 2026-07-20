import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle } from 'lucide-react';
import { useToast } from '../../../components/ui/ToastProvider';
import { useForceEndSession } from '../hooks/useSessionQueries';

interface ForceEndSessionDialogProps {
  sessionId: string | null;
  sessionTitle: string;
  onClose: () => void;
}

export const ForceEndSessionDialog = ({
  sessionId,
  sessionTitle,
  onClose,
}: ForceEndSessionDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const mutation = useForceEndSession();

  useEffect(() => {
    if (sessionId) {
      setReason('');
      setError('');
    }
  }, [sessionId]);

  if (!sessionId) return null;

  const submit = async () => {
    // Alternate path 5أ: a reason is required.
    if (reason.trim().length === 0) {
      setError(t('sessions.forceEnd.reasonRequired'));
      return;
    }
    setError('');

    try {
      const result = await mutation.mutateAsync({ sessionId, reason: reason.trim() });

      if (result.alreadyEnded) {
        // Alternate path 6ب: nothing to do.
        showToast({
          type: 'info',
          title: t('sessions.forceEnd.noActionTitle'),
          message: t('sessions.forceEnd.alreadyEnded'),
        });
      } else {
        // Step 8: report the outcome of each step (7أ makes partial success possible).
        const partial = !result.streamEnded || !result.summaryTriggered;
        showToast({
          type: partial ? 'warning' : 'success',
          title: t('sessions.forceEnd.successTitle'),
          message: partial
            ? t('sessions.forceEnd.partial', {
                stream: t(result.streamEnded ? 'sessions.forceEnd.ok' : 'sessions.forceEnd.failed'),
                summary: t(result.summaryTriggered ? 'sessions.forceEnd.ok' : 'sessions.forceEnd.failed'),
              })
            : t('sessions.forceEnd.success'),
        });
      }
      onClose();
    } catch (err: any) {
      const status = err?.response?.status;
      setError(
        status === 404
          ? t('sessions.forceEnd.notFound') // 6أ
          : err?.response?.data?.detail || t('sessions.forceEnd.fallbackError'),
      );
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="mb-4 flex items-start gap-3">
          <span className="rounded-lg bg-red-100 p-2 text-red-600 dark:bg-red-950/40 dark:text-red-400">
            <AlertTriangle size={20} />
          </span>
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">
              {t('sessions.forceEnd.title')}
            </h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('sessions.forceEnd.description', { title: sessionTitle })}
            </p>
          </div>
        </div>

        {error && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {error}
          </div>
        )}

        <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('sessions.forceEnd.reasonLabel')}
        </label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          autoFocus
          placeholder={t('sessions.forceEnd.reasonPlaceholder')}
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
            disabled={mutation.isPending}
            className="inline-flex items-center justify-center rounded-lg bg-red-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-red-700 disabled:opacity-50"
          >
            {mutation.isPending ? t('sessions.forceEnd.ending') : t('sessions.forceEnd.confirm')}
          </button>
        </div>
      </div>
    </div>
  );
};
