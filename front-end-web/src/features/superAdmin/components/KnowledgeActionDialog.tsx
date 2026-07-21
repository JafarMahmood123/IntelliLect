import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AlertTriangle, RefreshCw } from 'lucide-react';

interface KnowledgeActionDialogProps {
  open: boolean;
  variant: 'reindex' | 'delete';
  title: string;
  description: string;
  confirmLabel: string;
  pendingLabel: string;
  showFailedOnly?: boolean;
  /** Runs the action. Resolve to close; throw to show the message in the dialog. */
  onConfirm: (reason: string, failedOnly: boolean) => Promise<void>;
  onClose: () => void;
}

/**
 * Shared confirm dialog for the knowledge-base actions (reindex file / reindex classroom / delete).
 * Every action requires a reason (alt path 6أ). The classroom reindex adds a "failed only" toggle
 * to narrow the scope (7ب).
 */
export const KnowledgeActionDialog = ({
  open,
  variant,
  title,
  description,
  confirmLabel,
  pendingLabel,
  showFailedOnly = false,
  onConfirm,
  onClose,
}: KnowledgeActionDialogProps) => {
  const { t } = useTranslation('superAdmin');
  const [reason, setReason] = useState('');
  const [failedOnly, setFailedOnly] = useState(true);
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);

  useEffect(() => {
    if (open) {
      setReason('');
      setFailedOnly(true);
      setError('');
      setPending(false);
    }
  }, [open]);

  if (!open) return null;

  const submit = async () => {
    if (reason.trim().length === 0) {
      setError(t('knowledge.actions.reasonRequired')); // 6أ
      return;
    }
    setError('');
    setPending(true);
    try {
      await onConfirm(reason.trim(), failedOnly);
      onClose();
    } catch (err: any) {
      setPending(false);
      setError(err?.response?.data?.detail || err?.message || t('knowledge.actions.fallbackError'));
    }
  };

  const accent =
    variant === 'delete'
      ? 'bg-red-600 hover:bg-red-700'
      : 'bg-purple-600 hover:bg-purple-700';
  const iconWrap =
    variant === 'delete'
      ? 'bg-red-100 text-red-600 dark:bg-red-950/40 dark:text-red-400'
      : 'bg-purple-100 text-purple-600 dark:bg-purple-950/40 dark:text-purple-400';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl dark:border-slate-800 dark:bg-slate-900">
        <div className="mb-4 flex items-start gap-3">
          <span className={`rounded-lg p-2 ${iconWrap}`}>
            {variant === 'delete' ? <AlertTriangle size={20} /> : <RefreshCw size={20} />}
          </span>
          <div>
            <h2 className="text-lg font-semibold text-slate-900 dark:text-white">{title}</h2>
            <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">{description}</p>
          </div>
        </div>

        {showFailedOnly && (
          <label className="mb-3 flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
            <input
              type="checkbox"
              checked={failedOnly}
              onChange={(e) => setFailedOnly(e.target.checked)}
              className="h-4 w-4 rounded border-slate-300"
            />
            {t('knowledge.actions.failedOnly')}
          </label>
        )}

        {error && (
          <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-300">
            {error}
          </div>
        )}

        <label className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300">
          {t('knowledge.actions.reasonLabel')}
        </label>
        <textarea
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          rows={3}
          placeholder={t('knowledge.actions.reasonPlaceholder')}
          className="w-full rounded-lg border border-slate-200 bg-slate-50 px-4 py-2.5 text-sm text-slate-900 outline-none focus:border-violet-500 dark:border-slate-800 dark:bg-slate-950/50 dark:text-slate-100"
        />

        <div className="mt-5 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={pending}
            className="inline-flex items-center justify-center rounded-lg border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-100 disabled:opacity-50 dark:border-slate-800 dark:text-slate-200 dark:hover:bg-slate-900"
          >
            {t('common:buttons.cancel')}
          </button>
          <button
            type="button"
            onClick={submit}
            disabled={pending}
            className={`inline-flex items-center justify-center rounded-lg px-4 py-2.5 text-sm font-semibold text-white disabled:opacity-50 ${accent}`}
          >
            {pending ? pendingLabel : confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
};
