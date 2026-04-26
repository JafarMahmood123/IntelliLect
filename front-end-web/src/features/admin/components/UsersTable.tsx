import { useTranslation } from 'react-i18next';
import { Table, type TableColumn } from '../../../components/ui/Table';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import type { User } from '../../../types';

interface UsersTableProps {
  users: User[];
  isLoading: boolean;
  isError: boolean;
  renderActions: (user: User) => React.ReactNode;
}

export const UsersTable = ({ users, isLoading, isError, renderActions }: UsersTableProps) => {
  const { t } = useTranslation('admin');

  const columns: TableColumn<User>[] =[
    {
      key: 'name',
      header: t('table.name'),
      headerClassName: 'w-[24%] text-center', 
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
        <div className="truncate text-slate-600 dark:text-slate-400">{user.email}</div>
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
      headerClassName: 'w-[15%] text-center',
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
                second: '2-digit',
              })}
            </div>
          </div>
        );
      },
    },
    {
      key: 'actions',
      header: t('table.actions'),
      headerClassName: 'w-[15%] text-center',
      cellClassName: 'text-center',
      render: (user) => (
        <div className="flex justify-center">
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
    />
  );
};