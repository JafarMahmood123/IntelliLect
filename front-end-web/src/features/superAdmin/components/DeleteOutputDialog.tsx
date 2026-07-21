import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle } from 'lucide-react';
import { useToast } from '../../../components/ui/ToastProvider';
import { useDeleteOutput } from '../hooks/useOutputQueries';
import type { OutputItem } from '../types';

interface DeleteOutputDialogProps {
  output: OutputItem | null;
  onClose: () => void;
}

/** Steps 4-8: delete a recording or summary. Requires a reason (4أ); handles 5أ/5ب/6ب errors. */
export const DeleteOutputDialog = ({ output, onClose }: DeleteOutputDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const mutation = useDeleteOutput();

  useEffect(() => {
    if (output) {
      setReason('');
      setError('');
    }
  }, [output]);

  if (!output) return null;

  const typeLabel =
    output.type === 'Summary' ? t('outputs.types.summary') : t('outputs.types.recording');

  const submit = async () => {
    if (reason.trim().length === 0) {
      setError(t('outputs.delete.reasonRequired')); // 4أ
      return;
    }
    setError('');
    try {
      await mutation.mutateAsync({ type: output.type, outputId: output.outputId, reason: reason.trim() });
      showToast({
        type: 'success',
        title: t('outputs.delete.successTitle'),
        message: t('outputs.delete.success', { type: typeLabel }),
      });
      onClose();
    } catch (err: any) {
      const status = err?.response?.status;
      if (status === 404) {
        setError(t('outputs.delete.notFound')); // 5أ
      } else if (status === 409) {
        setError(t('outputs.delete.liveBlock')); // 5ب
      } else {
        setError(err?.response?.data?.detail || t('outputs.delete.fallbackError')); // includes 6ب
      }
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
              {t('outputs.delete.title', { type: typeLabel })}
            </h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
              {t('outputs.delete.description', { type: typeLabel, session: output.sessionTitle })}
            </p>
          </div>
        </div>

        {error && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {error}
          </div>
        )}

        <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('outputs.delete.reasonLabel')}
        </label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          placeholder={t('outputs.delete.reasonPlaceholder')}
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
            {mutation.isPending ? t('outputs.delete.deleting') : t('outputs.delete.confirm')}
          </button>
        </div>
      </div>
    </div>
  );
};
