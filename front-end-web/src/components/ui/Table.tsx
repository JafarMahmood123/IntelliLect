import React from 'react';

export interface TableColumn<T> {
  key: string;
  header: React.ReactNode;
  render: (row: T, index: number) => React.ReactNode;
  headerClassName?: string;
  cellClassName?: string;
}

interface TableProps<T> {
  data: T[];
  columns: TableColumn<T>[];
  rowKey: (row: T, index: number) => string;
  isLoading?: boolean;
  isError?: boolean;
  loadingText?: string;
  errorText?: string;
  emptyText?: string;
  containerClassName?: string;
  tableClassName?: string;
  rowClassName?: (row: T, index: number) => string;
  onRowClick?: (row: T, index: number) => void;
}

export const Table = <T,>({
  data,
  columns,
  rowKey,
  isLoading = false,
  isError = false,
  loadingText = 'Loading...',
  errorText = 'Something went wrong.',
  emptyText = 'No data found.',
  containerClassName = '',
  tableClassName = '',
  rowClassName,
  onRowClick,
}: TableProps<T>) => {
  const resolvedContainerClassName = [
    'bg-white dark:bg-gray-900 border dark:border-gray-800 rounded-lg shadow overflow-hidden',
    containerClassName,
  ]
    .filter(Boolean)
    .join(' ');

  const resolvedTableClassName = ['min-w-full text-left text-sm', tableClassName]
    .filter(Boolean)
    .join(' ');

  const handleRowKeyDown = (
    event: React.KeyboardEvent<HTMLTableRowElement>,
    row: T,
    index: number,
  ) => {
    if (!onRowClick) return;

    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onRowClick(row, index);
    }
  };

  if (isLoading) {
    return (
      <div className={resolvedContainerClassName}>
        <div className="p-6 text-slate-600 dark:text-slate-400">{loadingText}</div>
      </div>
    );
  }

  if (isError) {
    return (
      <div className={resolvedContainerClassName}>
        <div className="p-6 text-red-500">{errorText}</div>
      </div>
    );
  }

  return (
    <div className={resolvedContainerClassName}>
      <div className="overflow-x-auto">
        <table className={resolvedTableClassName}>
          <thead className="bg-gray-50 dark:bg-gray-800 border-b dark:border-gray-700">
            <tr>
              {columns.map((column) => (
                <th
                  key={column.key}
                  className={[
                    'p-4 font-semibold text-slate-700 dark:text-slate-200',
                    column.headerClassName ?? '',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                >
                  {column.header}
                </th>
              ))}
            </tr>
          </thead>

          <tbody>
            {data.length === 0 ? (
              <tr>
                <td
                  colSpan={columns.length}
                  className="p-8 text-center text-slate-500 dark:text-slate-400"
                >
                  {emptyText}
                </td>
              </tr>
            ) : (
              data.map((row, index) => {
                const customRowClassName = rowClassName?.(row, index);

                const resolvedRowClassName = [
                  customRowClassName ??
                    'border-b dark:border-gray-800 hover:bg-gray-50 dark:hover:bg-gray-800/50',
                  onRowClick
                    ? 'cursor-pointer focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500/50 focus-visible:ring-inset'
                    : '',
                ]
                  .filter(Boolean)
                  .join(' ');

                return (
                  <tr
                    key={rowKey(row, index)}
                    tabIndex={onRowClick ? 0 : undefined}
                    role={onRowClick ? 'button' : undefined}
                    onClick={onRowClick ? () => onRowClick(row, index) : undefined}
                    onKeyDown={(event) => handleRowKeyDown(event, row, index)}
                    className={resolvedRowClassName}
                  >
                    {columns.map((column) => (
                      <td
                        key={column.key}
                        className={[
                          'p-4 align-middle',
                          column.cellClassName ?? '',
                        ]
                          .filter(Boolean)
                          .join(' ')}
                      >
                        {column.render(row, index)}
                      </td>
                    ))}
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
};