import { useEffect } from 'react';
import { AlertTriangle, Loader2, X } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import ReactMarkdown from 'react-markdown';
import rehypeSanitize from 'rehype-sanitize';
import { getSummaryDownloadUrl } from '../api/summaries';

interface SummaryPreviewProps {
  isOpen: boolean;
  onClose: () => void;
  classroomId: string;
  summaryId: string;
  /** Human-readable label for the summary being previewed. */
  label: string;
}

/**
 * Fetches the summary's Markdown artifact on demand (pre-signed URL, then the
 * text) and renders it read-only and sanitized. Only runs while the panel is
 * open, so the list never pays this cost.
 */
const fetchSummaryMarkdown = async (
  classroomId: string,
  summaryId: string,
): Promise<string> => {
  const { url } = await getSummaryDownloadUrl(classroomId, summaryId, 'md');
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Failed to fetch summary markdown: ${response.status}`);
  }
  return response.text();
};

export const SummaryPreview = ({
  isOpen,
  onClose,
  classroomId,
  summaryId,
  label,
}: SummaryPreviewProps) => {
  const { t } = useTranslation('summaries');

  // Lock body scroll while open (matches ConfirmationModal).
  useEffect(() => {
    if (isOpen) document.body.style.overflow = 'hidden';
    else document.body.style.overflow = '';
    return () => {
      document.body.style.overflow = '';
    };
  }, [isOpen]);

  // Close on Escape.
  useEffect(() => {
    if (!isOpen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [isOpen, onClose]);

  const {
    data: markdown,
    isLoading,
    isError,
  } = useQuery({
    queryKey: ['summaries', classroomId, summaryId, 'preview'],
    queryFn: () => fetchSummaryMarkdown(classroomId, summaryId),
    enabled: isOpen,
    staleTime: 0,
    gcTime: 0,
    retry: false,
  });

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center p-4">
      <div
        className="fixed inset-0 bg-slate-950/60 backdrop-blur-sm transition-opacity"
        onClick={onClose}
        aria-hidden="true"
      />

      <div
        role="dialog"
        aria-modal="true"
        aria-label={t('preview.title')}
        className="relative flex max-h-[85vh] w-full max-w-2xl flex-col overflow-hidden rounded-xl border border-slate-200 bg-white text-left shadow-xl dark:border-slate-800 dark:bg-slate-900"
      >
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <div className="min-w-0">
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
              {t('preview.title')}
            </h3>
            <p className="truncate text-sm text-slate-500 dark:text-slate-400">
              {label}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={t('preview.close')}
            className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-slate-400 transition-colors hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-200"
          >
            <X size={18} aria-hidden="true" />
          </button>
        </div>

        <div className="overflow-y-auto px-6 py-5">
          {isLoading && (
            <div
              className="flex items-center justify-center gap-2 py-12 text-slate-500"
              role="status"
            >
              <Loader2 size={18} className="animate-spin" aria-hidden="true" />
              {t('preview.loading')}
            </div>
          )}

          {isError && (
            <div
              role="alert"
              className="flex flex-col items-center gap-3 py-12 text-center text-red-600 dark:text-red-400"
            >
              <AlertTriangle size={32} aria-hidden="true" />
              <p className="text-sm">{t('preview.error')}</p>
            </div>
          )}

          {!isLoading && !isError && markdown !== undefined && (
            <article
              dir="ltr"
              className="prose prose-slate max-w-none text-left text-sm leading-6 text-slate-700 dark:text-slate-200 [&_a]:text-violet-600 [&_code]:rounded [&_code]:bg-slate-100 [&_code]:px-1 [&_h1]:mb-3 [&_h1]:mt-4 [&_h1]:text-xl [&_h1]:font-bold [&_h2]:mb-2 [&_h2]:mt-4 [&_h2]:text-lg [&_h2]:font-semibold [&_li]:my-1 [&_p]:my-2 [&_ul]:list-disc [&_ul]:pl-5 dark:[&_code]:bg-slate-800"
            >
              {/* rehype-sanitize strips any unsafe HTML/attributes from the
                  Markdown before it is rendered. Content is English-only. */}
              <ReactMarkdown rehypePlugins={[rehypeSanitize]}>
                {markdown}
              </ReactMarkdown>
            </article>
          )}
        </div>
      </div>
    </div>
  );
};
