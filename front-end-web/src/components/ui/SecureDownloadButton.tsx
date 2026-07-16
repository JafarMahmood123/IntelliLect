import { useState } from 'react';
import { Download, Loader2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useToast } from './ToastProvider';

/** Minimal shape of a pre-signed download URL response (any artifact type). */
interface DownloadUrl {
  url: string;
  expiresAt: string;
}

interface SecureDownloadButtonProps {
  /**
   * Fetches a fresh, short-lived pre-signed URL. Called ONLY on click —
   * never on mount/render, because the URL expires quickly.
   */
  getUrl: () => Promise<DownloadUrl>;
  /** Visible button label. Defaults to the translated "Download". */
  label?: string;
  /** Accessible label describing what is being downloaded. */
  ariaLabel?: string;
  disabled?: boolean;
  className?: string;
}

/**
 * The single download primitive for all artifact types. On click it fetches
 * the pre-signed URL on demand and opens it to start the direct-from-S3
 * download, showing a loading state while fetching and a clear error on
 * failure. It intentionally does NOT fetch on mount.
 */
export const SecureDownloadButton = ({
  getUrl,
  label,
  ariaLabel,
  disabled = false,
  className = '',
}: SecureDownloadButtonProps) => {
  const { t } = useTranslation('recordings');
  const { showToast } = useToast();
  const [isPreparing, setIsPreparing] = useState(false);
  const [hasError, setHasError] = useState(false);

  const handleClick = async () => {
    if (isPreparing || disabled) return;

    setIsPreparing(true);
    setHasError(false);

    try {
      const { url } = await getUrl();
      // Open the pre-signed S3 URL to start the direct download.
      window.open(url, '_blank', 'noopener,noreferrer');
    } catch {
      setHasError(true);
      showToast({
        type: 'error',
        title: t('error.downloadFailedTitle'),
        message: t('error.downloadFailed'),
      });
    } finally {
      setIsPreparing(false);
    }
  };

  const text = label ?? t('download');

  return (
    <div className="flex flex-col items-end gap-1">
      <button
        type="button"
        onClick={handleClick}
        disabled={disabled || isPreparing}
        aria-label={ariaLabel ?? text}
        aria-busy={isPreparing}
        className={`inline-flex items-center justify-center gap-2 rounded-lg px-4 py-2.5 text-sm font-semibold text-white transition-all duration-300 bg-gradient-to-r from-violet-600 to-indigo-600 shadow-md hover:from-violet-700 hover:to-indigo-700 hover:shadow-lg hover:shadow-violet-500/50 dark:hover:shadow-violet-400/40 active:scale-[0.98] disabled:cursor-not-allowed disabled:opacity-50 disabled:active:scale-100 ${className}`}
      >
        {isPreparing ? (
          <Loader2 size={16} className="animate-spin" aria-hidden="true" />
        ) : (
          <Download size={16} aria-hidden="true" />
        )}
        {isPreparing ? t('preparing') : text}
      </button>

      {hasError && (
        <span role="alert" className="text-xs text-red-600 dark:text-red-400">
          {t('error.downloadFailed')}
        </span>
      )}
    </div>
  );
};
