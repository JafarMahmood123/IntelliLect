import { useTranslation } from 'react-i18next';
import { Table, type TableColumn } from '../../../components/ui/Table';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import type { User } from '../../../types';

interface UsersTableProps {
  users: User[];
  isLoading: boolean;
  isError: boolean;
  renderActions: (user: User) => React.ReactNode;
  onUserClick?: (user: User) => void;
  /**
   * Selection is opt-in: pass these three together to get a checkbox column, omit them for a
   * plain table. The rows shown are the only ones selectable — see the header checkbox note.
   */
  selectedIds?: ReadonlySet<string>;
  onToggleOne?: (userId: string) => void;
  onToggleAllOnPage?: (selectAll: boolean) => void;
}

export const UsersTable = ({
  users,
  isLoading,
  isError,
  renderActions,
  onUserClick,
  selectedIds,
  onToggleOne,
  onToggleAllOnPage,
}: UsersTableProps) => {
  const { t } = useTranslation('admin');

  const isSelectable = Boolean(selectedIds && onToggleOne && onToggleAllOnPage);
  const selectedOnPage = users.filter((user) => selectedIds?.has(user.id)).length;
  const allOnPageSelected = users.length > 0 && selectedOnPage === users.length;

  const selectionColumn: TableColumn<User> = {
    key: 'select',
    headerClassName: 'w-[5%] text-center',
    cellClassName: 'text-center',
    // The header checkbox covers THIS PAGE only, never every account matching the filter —
    // those are different features, and the one that acts on rows you cannot see is the
    // dangerous one. The label says so explicitly.
    header: (
      <input
        type="checkbox"
        className="h-4 w-4 cursor-pointer accent-violet-600"
        checked={allOnPageSelected}
        // Some rows but not all: neither checked nor unchecked.
        ref={(el) => {
          if (el) el.indeterminate = selectedOnPage > 0 && !allOnPageSelected;
        }}
        disabled={users.length === 0}
        onChange={(event) => onToggleAllOnPage?.(event.target.checked)}
        aria-label={t('bulk.selectAllOnPage')}
      />
    ),
    render: (user) => (
      <div
        className="flex justify-center"
        // Selecting must not also open the details drawer.
        onClick={(event) => event.stopPropagation()}
        onKeyDown={(event) => event.stopPropagation()}
      >
        <input
          type="checkbox"
          className="h-4 w-4 cursor-pointer accent-violet-600"
          checked={selectedIds?.has(user.id) ?? false}
          onChange={() => onToggleOne?.(user.id)}
          aria-label={t('bulk.selectOne', {
            name: `${user.firstName} ${user.lastName}`.trim(),
          })}
        />
      </div>
    ),
  };

  const columns: TableColumn<User>[] = [
    ...(isSelectable ? [selectionColumn] : []),
    {
      key: 'name',
      header: t('table.name'),
      headerClassName: 'w-[20%] text-center',
      cellClassName: 'text-left',
      render: (user) => (
        <div>
          <div className="truncate font-medium text-slate-900 dark:text-slate-100">
            {user.firstName} {user.lastName}
          </div>
          <div className="truncate text-xs text-slate-500 dark:text-slate-400">
            @{user.userName}
          </div>
        </div>
      ),
    },
    {
      key: 'email',
      header: t('table.email'),
      headerClassName: 'w-[22%] text-center',
      cellClassName: 'text-left',
      render: (user) => (
        <div className="truncate text-slate-600 dark:text-slate-400">
          {user.email}
        </div>
      ),
    },
    {
      key: 'role',
      header: t('table.role'),
      headerClassName: 'w-[12%] text-center',
      cellClassName: 'text-center',
      render: (user) => (
        <div className="flex justify-center text-slate-600 dark:text-slate-400">
          {user.roleName}
        </div>
      ),
    },
    {
      key: 'status',
      header: t('table.status'),
      headerClassName: 'w-[12%] text-center',
      cellClassName: 'text-center',
      render: (user) => (
        <div className="flex justify-center">
          <StatusBadge status={user.status} />
        </div>
      ),
    },
    {
      key: 'joined',
      header: t('table.joined'),
      headerClassName: 'w-[14%] text-center',
      cellClassName: 'text-center',
      render: (user) => {
        const joinedDate = new Date(user.createdAtUtc);

        return (
          <div className="flex flex-col items-center justify-center">
            <div className="text-slate-600 dark:text-slate-400">
              {joinedDate.toLocaleDateString()}
            </div>
            <div className="mt-1 text-xs text-slate-500">
              {joinedDate.toLocaleTimeString([], {
                hour: '2-digit',
                minute: '2-digit',
              })}
            </div>
          </div>
        );
      },
    },
    {
      key: 'actions',
      header: t('table.actions'),
      headerClassName: 'w-[20%] text-center',
      cellClassName: 'text-center',
      render: (user) => (
        <div
          className="flex justify-center"
          onClick={(event) => event.stopPropagation()}
          onKeyDown={(event) => event.stopPropagation()}
        >
          {renderActions(user)}
        </div>
      ),
    },
  ];

  return (
    <Table
      tableClassName="table-fixed"
      data={users}
      columns={columns}
      rowKey={(user) => user.id}
      isLoading={isLoading}
      isError={isError}
      loadingText={t('loading')}
      errorText={t('loadError')}
      emptyText={t('empty')}
      onRowClick={onUserClick ? (user) => onUserClick(user) : undefined}
    />
  );
};