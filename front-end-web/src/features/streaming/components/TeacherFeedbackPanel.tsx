import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, Sparkles, X } from 'lucide-react';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import { useAuthStore } from '../../../store/useAuthStore';
import { useTeachingSuggestions } from '../hooks/useTeachingSuggestions';
import type { SuggestionSource, TeachingSuggestion } from '../types';

/**
 * Private, teacher-only panel showing real-time AI teaching-assistant
 * suggestions over the EXISTING LiveKit room. Self-gates by role (defense in
 * depth) — renders nothing and attaches no listener for non-teachers.
 */
export const TeacherFeedbackPanel = () => {
  const { t, i18n } = useTranslation('streaming');
  const { user } = useAuthStore();
  const isTeacher = user?.roleName === 'Teacher';

  // Hooks must run unconditionally; the hook itself no-ops when not enabled.
  const { suggestions, dismiss } = useTeachingSuggestions(isTeacher);

  const [collapsed, setCollapsed] = useState(false);
  const [unread, setUnread] = useState(0);
  const seenIds = useRef<Set<string>>(new Set());

  // Track unread suggestions that arrive while the panel is collapsed.
  useEffect(() => {
    const fresh = suggestions.filter((item) => !seenIds.current.has(item.id));
    if (fresh.length === 0) return;
    seenIds.current = new Set(suggestions.map((item) => item.id));
    if (collapsed) setUnread((count) => count + fresh.length);
  }, [suggestions, collapsed]);

  const toggleCollapsed = () => {
    // Expanding clears the unread indicator; done in the handler (not an
    // effect) to avoid a cascading setState-in-effect.
    if (collapsed) setUnread(0);
    setCollapsed((value) => !value);
  };

  // Hard rule: never render for students.
  if (!isTeacher) return null;

  const formatTime = (iso: string) => {
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return '';
    return date.toLocaleTimeString(i18n.language, {
      hour: '2-digit',
      minute: '2-digit',
    });
  };

  return (
    <section
      aria-label={t('feedback.title')}
      className="pointer-events-auto absolute top-4 z-40 flex w-80 max-w-[calc(100%-2rem)] flex-col overflow-hidden rounded-2xl border border-white/10 bg-slate-900/95 shadow-2xl backdrop-blur ltr:right-4 rtl:left-4"
    >
      <div className="flex items-center justify-between gap-2 border-b border-white/5 bg-slate-950/60 px-4 py-3">
        <div className="flex min-w-0 items-center gap-2">
          <Sparkles size={16} className="shrink-0 text-violet-400" aria-hidden="true" />
          <div className="min-w-0">
            <p className="truncate text-sm font-bold text-white">
              {t('feedback.title')}
            </p>
            <p className="truncate text-[10px] font-medium uppercase tracking-wider text-slate-400">
              {t('feedback.subtitle')}
            </p>
          </div>
        </div>

        <div className="flex items-center gap-2">
          {collapsed && unread > 0 && (
            <span
              className="rounded-full bg-violet-600 px-2 py-0.5 text-[10px] font-bold text-white"
              aria-hidden="true"
            >
              {t('feedback.unread', { count: unread })}
            </span>
          )}
          <button
            type="button"
            onClick={toggleCollapsed}
            aria-expanded={!collapsed}
            aria-label={collapsed ? t('feedback.expand') : t('feedback.collapse')}
            className="flex h-7 w-7 items-center justify-center rounded-lg text-slate-400 transition-colors hover:bg-white/10 hover:text-white"
          >
            <ChevronDown
              size={16}
              className={`transition-transform ${collapsed ? '-rotate-90' : ''}`}
              aria-hidden="true"
            />
          </button>
        </div>
      </div>

      {!collapsed && (
        <div
          className="custom-scrollbar max-h-[60vh] space-y-3 overflow-y-auto p-3"
          aria-live="polite"
          aria-relevant="additions"
        >
          {suggestions.length === 0 ? (
            <p className="px-2 py-6 text-center text-xs italic text-slate-500">
              {t('feedback.empty')}
            </p>
          ) : (
            suggestions.map((suggestion) => (
              <SuggestionCard
                key={suggestion.id}
                suggestion={suggestion}
                timeLabel={formatTime(suggestion.createdAt)}
                onDismiss={() => dismiss(suggestion.id)}
              />
            ))
          )}
        </div>
      )}
    </section>
  );
};

interface SuggestionCardProps {
  suggestion: TeachingSuggestion;
  timeLabel: string;
  onDismiss: () => void;
}

const SuggestionCard = ({
  suggestion,
  timeLabel,
  onDismiss,
}: SuggestionCardProps) => {
  const { t } = useTranslation('streaming');

  return (
    <article className="rounded-xl border border-white/5 bg-slate-800/60 p-3 shadow-sm">
      <div className="mb-2 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <StatusBadge status={suggestion.feedbackType} />
          {timeLabel && (
            <span className="text-[10px] font-medium text-slate-500">
              {timeLabel}
            </span>
          )}
        </div>
        <button
          type="button"
          onClick={onDismiss}
          aria-label={t('feedback.dismiss')}
          className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md text-slate-400 transition-colors hover:bg-white/10 hover:text-white"
        >
          <X size={14} aria-hidden="true" />
        </button>
      </div>

      <p className="text-sm leading-snug text-slate-100">{suggestion.text}</p>

      {suggestion.sources.length > 0 && (
        <div className="mt-3">
          <p className="sr-only">{t('feedback.sourcesLabel')}</p>
          <ul className="flex flex-wrap gap-1.5">
            {suggestion.sources.map((source) => (
              <li key={`${source.citation}-${source.documentId}`}>
                <span className="inline-flex items-center gap-1 rounded-md bg-white/5 px-2 py-1 text-[10px] font-medium text-slate-300">
                  <span className="font-bold text-violet-300">
                    [{source.citation}]
                  </span>
                  {formatSourceLocator(source, t)}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </article>
  );
};

/** Builds a compact, human-readable locator for a citation chip. */
const formatSourceLocator = (
  source: SuggestionSource,
  t: (key: string, options?: Record<string, unknown>) => string,
): string => {
  if (source.slide !== null) return t('feedback.source.slide', { n: source.slide });
  if (source.page !== null) return t('feedback.source.page', { n: source.page });
  if (source.section) return source.section;
  return '';
};
