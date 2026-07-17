import { useTranslation } from 'react-i18next';

interface StatusBadgeProps {
  status: string | number; // Accepts both string labels and numeric enums
  /**
   * Optional explicit label. When set it overrides the i18n-derived text while
   * the color still follows `status` — lets callers (e.g. file indexing) show a
   * domain-specific label ("Ready"/"Indexing…") without new color logic.
   */
  label?: string;
}

// Numeric session enums (kept for backward compatibility with existing callers).
const numericStatusMap: Record<number, string> = {
  0: 'Scheduled',
  1: 'Live',
  2: 'Ended',
};

// Color grouping by normalized status. Reused across recordings, summaries,
// sessions, etc. — all statuses route through the same design tokens.
const successStatuses = ['active', 'live', 'available', 'done', 'completed'];
const pendingStatuses = [
  'pending',
  'scheduled',
  'processing',
  'generating',
  'gap',
];
const errorStatuses = [
  'deactivated',
  'inactive',
  'rejected',
  'ended',
  'failed',
  'cancelled',
  'discrepancy',
];

const colorClassesFor = (normalizedStatus: string): string => {
  if (successStatuses.includes(normalizedStatus)) {
    return 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400';
  }
  if (pendingStatuses.includes(normalizedStatus)) {
    return 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400';
  }
  if (errorStatuses.includes(normalizedStatus)) {
    return 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400';
  }
  return 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300';
};

export const StatusBadge = ({ status, label }: StatusBadgeProps) => {
  const { t } = useTranslation('common');

  const statusString =
    typeof status === 'number'
      ? numericStatusMap[status] ?? 'Unknown'
      : status || 'Unknown';

  const normalizedStatus = statusString.toLowerCase();

  // Explicit label wins; otherwise the i18n label with a graceful fallback to
  // the raw status string, so callers without a translation key still render.
  const text =
    label ?? t(`statuses.${normalizedStatus}`, { defaultValue: statusString });

  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium ${colorClassesFor(
        normalizedStatus,
      )}`}
    >
      {text}
    </span>
  );
};
