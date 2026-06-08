import { ChevronLeft, ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from './Button';

interface PaginationProps {
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
  onPageChange: (page: number) => void;
}

export const Pagination = ({
  pageNumber,
  pageSize,
  totalCount,
  totalPages,
  hasPreviousPage,
  hasNextPage,
  onPageChange,
}: PaginationProps) => {
  const { t } = useTranslation('common');

  const safeTotalPages = Math.max(totalPages, 1);
  const safePageNumber = Math.min(Math.max(pageNumber, 1), safeTotalPages);

  const from = totalCount === 0 ? 0 : (safePageNumber - 1) * pageSize + 1;
  const to = totalCount === 0 ? 0 : Math.min(safePageNumber * pageSize, totalCount);

  const canGoPrevious = hasPreviousPage && safePageNumber > 1;
  const canGoNext = hasNextPage && safePageNumber < safeTotalPages;

  return (
    <div className="mt-4 flex flex-col gap-4 rounded-lg border border-slate-200 bg-white px-4 py-3 shadow-sm dark:border-slate-800 dark:bg-slate-900 sm:flex-row sm:items-center sm:justify-between">
      <div className="text-sm text-slate-600 dark:text-slate-400">
        {t('pagination.showing', {
          from,
          to,
          total: totalCount,
        })}
      </div>

      <div className="flex items-center justify-end gap-2">
        <Button
          variant="secondary"
          disabled={!canGoPrevious}
          onClick={() => onPageChange(safePageNumber - 1)}
          className="h-10 px-3"
          aria-label={t('pagination.previous')}
        >
          <ChevronLeft size={16} />
          <span>{t('pagination.previous')}</span>
        </Button>

        <span className="min-w-28 text-center text-sm font-medium text-slate-700 dark:text-slate-200">
          {t('pagination.pageOf', {
            page: safePageNumber,
            totalPages: safeTotalPages,
          })}
        </span>

        <Button
          variant="secondary"
          disabled={!canGoNext}
          onClick={() => onPageChange(safePageNumber + 1)}
          className="h-10 px-3"
          aria-label={t('pagination.next')}
        >
          <span>{t('pagination.next')}</span>
          <ChevronRight size={16} />
        </Button>
      </div>
    </div>
  );
};