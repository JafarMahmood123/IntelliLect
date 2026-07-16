import type { ReactNode } from 'react';
import { AlertTriangle, Inbox, RefreshCw } from 'lucide-react';
import { useTranslation } from 'react-i18next';

interface ArtifactListProps {
  isLoading: boolean;
  isError: boolean;
  isEmpty: boolean;
  children: ReactNode;
  emptyTitle: string;
  emptyDescription?: string;
  emptyIcon?: ReactNode;
  errorTitle?: string;
  errorDescription?: string;
  onRetry?: () => void;
  /** Number of skeleton rows to show while loading. */
  skeletonCount?: number;
}

/**
 * A small list shell that owns the loading / empty / error UX so every
 * artifact list (recordings, and later summaries/documents) shares the same
 * states. Renders `children` only in the loaded, non-empty case.
 */
export const ArtifactList = ({
  isLoading,
  isError,
  isEmpty,
  children,
  emptyTitle,
  emptyDescription,
  emptyIcon,
  errorTitle,
  errorDescription,
  onRetry,
  skeletonCount = 3,
}: ArtifactListProps) => {
  const { t } = useTranslation('common');

  if (isLoading) {
    return (
      <div
        className="grid gap-4"
        role="status"
        aria-live="polite"
        aria-busy="true"
      >
        <span className="sr-only">{t('buttons.loading')}</span>
        {Array.from({ length: skeletonCount }).map((_, index) => (
          <div
            key={index}
            className="h-24 animate-pulse rounded-2xl border border-slate-200 bg-slate-100 dark:border-slate-800 dark:bg-slate-900/50"
            aria-hidden="true"
          />
        ))}
      </div>
    );
  }

  if (isError) {
    return (
      <div
        role="alert"
        className="rounded-xl border border-red-200 bg-red-50 p-8 text-center text-red-600 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400"
      >
        <AlertTriangle className="mx-auto mb-4" size={40} aria-hidden="true" />
        <p className="text-lg font-bold">{errorTitle}</p>
        {errorDescription && (
          <p className="mt-1 text-sm">{errorDescription}</p>
        )}
        {onRetry && (
          <button
            type="button"
            onClick={onRetry}
            className="mt-4 inline-flex items-center gap-2 rounded-lg border border-red-200 bg-white px-4 py-2 text-sm font-semibold text-red-600 transition-colors hover:bg-red-50 dark:border-red-900/50 dark:bg-red-950/40 dark:text-red-400 dark:hover:bg-red-950/60"
          >
            <RefreshCw size={16} aria-hidden="true" />
            {t('actions.retry', { defaultValue: 'Retry' })}
          </button>
        )}
      </div>
    );
  }

  if (isEmpty) {
    return (
      <div className="py-12 text-center">
        <div className="mx-auto mb-4 text-slate-300 dark:text-slate-600">
          {emptyIcon ?? <Inbox className="mx-auto" size={48} aria-hidden="true" />}
        </div>
        <p className="font-medium text-slate-600 dark:text-slate-300">
          {emptyTitle}
        </p>
        {emptyDescription && (
          <p className="mt-1 text-sm italic text-slate-500">{emptyDescription}</p>
        )}
      </div>
    );
  }

  return <div className="grid gap-4">{children}</div>;
};
