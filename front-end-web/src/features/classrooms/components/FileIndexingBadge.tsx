import { useTranslation } from 'react-i18next';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import { useFileIndexingStatus } from '../hooks/useClassroomQueries';
import type { FileIndexingStatus } from '../types';

interface FileIndexingBadgeProps {
  classroomId: string;
  fileId: string;
}

// Maps a raw indexing status to the label i18n key. Pending and Processing both
// read as "Indexing…" so members see one calm in-progress state.
const labelKeyFor = (status: FileIndexingStatus): string => {
  switch (status) {
    case 'Done':
      return 'indexing.ready';
    case 'Failed':
      return 'indexing.failed';
    default:
      return 'indexing.inProgress';
  }
};

/**
 * Per-file RAG indexing badge. Reuses the F-0 StatusBadge (color follows the
 * status; label is the indexing-specific i18n string) and polls until terminal.
 * Status is conveyed by text, not color alone.
 */
export const FileIndexingBadge = ({ classroomId, fileId }: FileIndexingBadgeProps) => {
  const { t } = useTranslation('common');
  const { data, isLoading, isError } = useFileIndexingStatus(classroomId, fileId);

  // Stay quiet on first load / transient errors so a row never breaks.
  if (isLoading || isError || !data) {
    return null;
  }

  const label = t(labelKeyFor(data.status));

  return (
    <span role="status" aria-live="polite" aria-label={t('indexing.label', { status: label })}>
      <StatusBadge status={data.status} label={label} />
    </span>
  );
};
