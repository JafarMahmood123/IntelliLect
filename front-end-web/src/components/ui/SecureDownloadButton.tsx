import { useState } from 'react';
import { Download, Loader2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useToast } from './ToastProvider';

type DownloadButtonVariant = 'primary' | 'secondary';

interface SecureDownloadButtonProps {
  /**
   * Performs the download. Called ONLY on click — typically streams the artifact through the API
   * (auth-guarded) and saves it. Shows a loading state while it runs and a toast on failure.
   */
  onDownload: () => Promise<void>;
  /** Visible button label. Defaults to the translated "Download". */
  label?: string;
  /** Accessible label describing what is being downloaded. */
  ariaLabel?: string;
  /** Visual weight — `primary` (default) or a lighter `secondary`. */
  variant?: DownloadButtonVariant;
  disabled?: boolean;
  className?: string;
}

const variantClasses: Record<DownloadButtonVariant, string> = {
  primary:
    'text-white bg-gradient-to-r from-violet-600 to-indigo-600 shadow-md hover:from-violet-700 hover:to-indigo-700 hover:shadow-lg hover:shadow-violet-500/50 dark:hover:shadow-violet-400/40',
  secondary:
    'bg-slate-100 text-slate-900 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-100 dark:hover:bg-slate-700',
};

/**
 * The single download primitive for all artifact types. On click it fetches
 * the pre-signed URL on demand and opens it to start the direct-from-S3
 * download, showing a loading state while fetching and a clear error on
 * failure. It intentionally does NOT fetch on mount.
 */
export const SecureDownloadButton = ({
  onDownload,
  label,
  ariaLabel,
  variant = 'primary',
  disabled = false,
  className = '',
}: SecureDownloadButtonProps) => {
  const { t } = useTranslation('common');
  const { showToast } = useToast();
  const [isPreparing, setIsPreparing] = useState(false);
  const [hasError, setHasError] = useState(false);

  const handleClick = async () => {
    if (isPreparing || disabled) return;

    setIsPreparing(true);
    setHasError(false);

    try {
      await onDownload();
    } catch {
      setHasError(true);
      showToast({
        type: 'error',
        title: t('download.errorTitle'),
        message: t('download.error'),
      });
    } finally {
      setIsPreparing(false);
    }
  };

  const text = label ?? t('download.label');

  return (
    <div className="flex flex-col items-end gap-1">
      <button
        type="button"
        onClick={handleClick}
        disabled={disabled || isPreparing}
        aria-label={ariaLabel ?? text}
        aria-busy={isPreparing}
        className={`inline-flex items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-semibold transition-all duration-300 active:scale-[0.98] disabled:cursor-not-allowed disabled:opacity-50 disabled:active:scale-100 ${variantClasses[variant]} ${className}`}
      >
        {isPreparing ? (
          <Loader2 size={16} className="animate-spin" aria-hidden="true" />
        ) : (
          <Download size={16} aria-hidden="true" />
        )}
        {isPreparing ? t('download.preparing') : text}
      </button>

      {hasError && (
        <span role="alert" className="text-xs text-red-600 dark:text-red-400">
          {t('download.error')}
        </span>
      )}
    </div>
  );
};
