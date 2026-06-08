import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import {
  AtSign,
  CalendarDays,
  Mail,
  Shield,
  UserCircle,
  UserRound,
  UserCheck,
  UserX,
  ShieldAlert,
  ShieldCheck,
} from 'lucide-react';
import { Drawer } from '../../../components/ui/Drawer';
import { Button } from '../../../components/ui/Button';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import type { User } from '../../../types';

type AdminTab = 'pending' | 'all';

interface UserDetailsDrawerProps {
  user: User | null;
  isOpen: boolean;
  activeTab: AdminTab;
  isMutating: boolean;
  onClose: () => void;
  onApprove: (user: User) => void;
  onReject: (user: User) => void;
  onDeactivate: (user: User) => void;
  onReactivate: (user: User) => void;
}

interface DetailItemProps {
  icon: ReactNode;
  label: string;
  value: ReactNode;
}

const DetailItem = ({ icon, label, value }: DetailItemProps) => {
  return (
    <div className="flex gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-900/70">
      <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">
        {icon}
      </div>

      <div className="min-w-0">
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
          {label}
        </p>
        <div className="mt-1 break-words text-sm font-medium text-slate-900 dark:text-slate-100">
          {value}
        </div>
      </div>
    </div>
  );
};

export const UserDetailsDrawer = ({
  user,
  isOpen,
  activeTab,
  isMutating,
  onClose,
  onApprove,
  onReject,
  onDeactivate,
  onReactivate,
}: UserDetailsDrawerProps) => {
  const { t } = useTranslation('admin');

  const getRoleLabel = (roleName: string) => {
    const key = `roles.${roleName}`;
    const translated = t(key);

    return translated === key ? roleName : translated;
  };

  const fullName = user ? `${user.firstName} ${user.lastName}`.trim() : '';
  const joinedDate = user ? new Date(user.createdAtUtc) : null;

  const formattedJoinedDate = joinedDate
    ? `${joinedDate.toLocaleDateString()} ${joinedDate.toLocaleTimeString([], {
        hour: '2-digit',
        minute: '2-digit',
      })}`
    : '-';

  const hasPendingActions = user && activeTab === 'pending';
  const canDeactivate = user && activeTab === 'all' && user.status === 'Active';
  const canReactivate =
    user && activeTab === 'all' && user.status !== 'Active' && user.status !== 'Pending';

  const hasActions = hasPendingActions || canDeactivate || canReactivate;

  const footer = user ? (
    <div className="flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-between">
      <Button
        variant="secondary"
        onClick={onClose}
        disabled={isMutating}
        className="sm:w-auto"
      >
        {t('common:buttons.close')}
      </Button>

      {hasActions ? (
        <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          {hasPendingActions && (
            <>
              <Button
                variant="ghost"
                disabled={isMutating}
                className="border border-red-200 bg-red-50 !text-red-600 hover:border-red-300 hover:!bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:!text-red-400 dark:hover:!bg-red-950/50"
                onClick={() => onReject(user)}
              >
                <UserX size={16} />
                {t('actions.reject')}
              </Button>

              <Button
                variant="ghost"
                disabled={isMutating}
                className="border border-green-200 bg-green-50 !text-green-600 shadow-none hover:border-green-300 hover:!bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:!text-green-400 dark:hover:!bg-green-950/50"
                onClick={() => onApprove(user)}
              >
                <UserCheck size={16} />
                {t('actions.approve')}
              </Button>
            </>
          )}

          {canDeactivate && (
            <Button
              variant="ghost"
              disabled={isMutating}
              className="border border-red-200 bg-red-50 !text-red-600 hover:border-red-300 hover:!bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:!text-red-400 dark:hover:!bg-red-950/50"
              onClick={() => onDeactivate(user)}
            >
              <ShieldAlert size={16} />
              {t('actions.deactivate')}
            </Button>
          )}

          {canReactivate && (
            <Button
              variant="ghost"
              disabled={isMutating}
              className="border border-green-200 bg-green-50 !text-green-600 hover:border-green-300 hover:!bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:!text-green-400 dark:hover:!bg-green-950/50"
              onClick={() => onReactivate(user)}
            >
              <ShieldCheck size={16} />
              {t('actions.reactivate')}
            </Button>
          )}
        </div>
      ) : (
        <p className="text-sm text-slate-500 dark:text-slate-400">
          {t('drawer.noActions')}
        </p>
      )}
    </div>
  ) : undefined;

  return (
    <Drawer
      isOpen={isOpen}
      onClose={onClose}
      title={t('drawer.title')}
      description={t('drawer.description')}
      icon={<UserCircle size={22} />}
      footer={footer}
      widthClassName="max-w-2xl"
    >
      {!user ? (
        <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm text-slate-600 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-400">
          {t('drawer.empty')}
        </div>
      ) : (
        <div className="space-y-8">
          <section>
            <div className="mb-4">
              <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
                {t('drawer.sections.identity')}
              </h3>
            </div>

            <div className="grid gap-4">
              <DetailItem
                icon={<UserRound size={18} />}
                label={t('drawer.fields.fullName')}
                value={fullName || '-'}
              />

              <DetailItem
                icon={<AtSign size={18} />}
                label={t('drawer.fields.username')}
                value={`@${user.userName}`}
              />

              <DetailItem
                icon={<Mail size={18} />}
                label={t('drawer.fields.email')}
                value={user.email}
              />
            </div>
          </section>

          <section>
            <div className="mb-4">
              <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
                {t('drawer.sections.access')}
              </h3>
            </div>

            <div className="grid gap-4">
              <DetailItem
                icon={<Shield size={18} />}
                label={t('drawer.fields.role')}
                value={getRoleLabel(user.roleName)}
              />

              <DetailItem
                icon={<ShieldCheck size={18} />}
                label={t('drawer.fields.status')}
                value={<StatusBadge status={user.status} />}
              />

              <DetailItem
                icon={<CalendarDays size={18} />}
                label={t('drawer.fields.joinedAt')}
                value={formattedJoinedDate}
              />
            </div>
          </section>
        </div>
      )}
    </Drawer>
  );
};