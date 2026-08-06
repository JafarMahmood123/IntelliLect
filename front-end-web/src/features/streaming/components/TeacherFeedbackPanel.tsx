import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AlertTriangle,
  Check,
  ChevronDown,
  CircleHelp,
  Sparkles,
  X,
} from 'lucide-react';
import { useAuthStore } from '../../../store/useAuthStore';
import { useTeachingSuggestions } from '../hooks/useTeachingSuggestions';
import type {
  FeedbackSeverity,
  SuggestionSource,
  TeachingSuggestion,
} from '../types';

/**
 * The translucent layers a label is read against, outermost first (test-plan H-10).
 *
 * Every one of these is semi-transparent, so the colour behind a chip is a composite of all
 * three and of whatever video frame is underneath. Naming them here rather than only in the
 * markup is what lets `TeacherFeedbackPanel.contrast.test.ts` compute the contrast a teacher
 * actually sees instead of the contrast of the topmost layer alone.
 */
export const SURFACES = {
  panel: 'bg-slate-900/95',
  header: 'bg-slate-950/60',
  card: 'bg-slate-800/60',
} as const;

/** Text colours on those surfaces, so the same test can check them too. */
export const TEXT_ON_SURFACE = {
  body: 'text-slate-100',
  meta: 'text-slate-400',
  subtitle: 'text-slate-400',
  emptyState: 'text-slate-400',
} as const;

/**
 * How each severity is shown. Colour is never the only carrier: every entry pairs its palette
 * with an icon AND a written label, so the card still reads correctly in greyscale, to a
 * colour-blind teacher, and to a screen reader. A teacher glancing at this mid-sentence has no
 * time to decode a hue.
 */
export const SEVERITY_STYLES: Record<
  FeedbackSeverity,
  { icon: typeof AlertTriangle; chip: string }
> = {
  incorrect: {
    icon: AlertTriangle,
    chip: 'bg-red-500/15 text-red-300 ring-1 ring-inset ring-red-500/30',
  },
  likely: {
    icon: CircleHelp,
    chip: 'bg-amber-500/15 text-amber-300 ring-1 ring-inset ring-amber-500/30',
  },
  missing: {
    icon: Sparkles,
    chip: 'bg-slate-500/15 text-slate-300 ring-1 ring-inset ring-slate-500/30',
  },
};

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
            <p className={`px-2 py-6 text-center text-xs italic ${TEXT_ON_SURFACE.emptyState}`}>
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
  const severity = SEVERITY_STYLES[suggestion.severity];
  const SeverityIcon = severity.icon;

  return (
    <article className="rounded-xl border border-white/5 bg-slate-800/60 p-3 shadow-sm">
      <div className="mb-2 flex items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <span
            className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold ${severity.chip}`}
          >
            <SeverityIcon size={11} aria-hidden="true" />
            {t(`feedback.severity.${suggestion.severity}`)}
          </span>
          {timeLabel && (
            <span className={`text-[10px] font-medium ${TEXT_ON_SURFACE.meta}`}>
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

      {/* Backend content (English for now): auto-direction so it renders cleanly
          even when the panel chrome is RTL. */}
      <p dir="auto" className="text-sm leading-snug text-slate-100">
        {suggestion.text}
      </p>

      {suggestion.incorrectText && (
        <CorrectionSpan
          incorrectText={suggestion.incorrectText}
          correctedText={suggestion.correctedText}
        />
      )}

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

interface CorrectionSpanProps {
  incorrectText: string;
  correctedText: string | null;
}

/**
 * The wrong words and their replacement, side by side.
 *
 * This is the part of the card a teacher reads in a glance mid-sentence, so it does not rely on
 * red-vs-green alone: each row carries an icon and a written label ("You said" / "Should be"),
 * and the wrong text is struck through. Any one of those cues is enough on its own.
 *
 * Renders only when the server sent a span it had verified against the lecture, so the quoted
 * words are always words the teacher actually said.
 */
const CorrectionSpan = ({ incorrectText, correctedText }: CorrectionSpanProps) => {
  const { t } = useTranslation('streaming');

  return (
    <dl className="mt-3 space-y-1.5 rounded-lg bg-slate-900/60 p-2.5">
      <div className="flex items-start gap-2">
        <dt className="flex shrink-0 items-center gap-1 pt-px text-[10px] font-semibold uppercase tracking-wide text-red-400">
          <X size={11} aria-hidden="true" />
          {t('feedback.correction.said')}
        </dt>
        <dd
          dir="auto"
          className="min-w-0 break-words text-xs font-medium text-red-300 line-through decoration-red-500/60"
        >
          {incorrectText}
        </dd>
      </div>

      {correctedText && (
        <div className="flex items-start gap-2">
          <dt className="flex shrink-0 items-center gap-1 pt-px text-[10px] font-semibold uppercase tracking-wide text-emerald-400">
            <Check size={11} aria-hidden="true" />
            {t('feedback.correction.shouldBe')}
          </dt>
          <dd dir="auto" className="min-w-0 break-words text-xs font-medium text-emerald-300">
            {correctedText}
          </dd>
        </div>
      )}
    </dl>
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
