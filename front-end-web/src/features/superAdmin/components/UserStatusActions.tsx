import { useTranslation } from 'react-i18next';
import { Check, Power, PowerOff, X } from 'lucide-react';
import { useToast } from '../../../components/ui/ToastProvider';
import { useAuthStore } from '../../../store/useAuthStore';
import { useChangeUserStatus } from '../hooks/useUserQueries';
import { getStatusActions, type UserStatusAction } from '../types';

interface UserStatusActionsProps {
  userId: string;
  status: string;
  size?: 'sm' | 'md';
}

const ACTION_META: Record<
  UserStatusAction,
  { icon: typeof Check; tone: 'positive' | 'destructive'; destructive: boolean }
> = {
  Accept: { icon: Check, tone: 'positive', destructive: false },
  Reactivate: { icon: Power, tone: 'positive', destructive: false },
  Reject: { icon: X, tone: 'destructive', destructive: true },
  Deactivate: { icon: PowerOff, tone: 'destructive', destructive: true },
};

const toneClasses = (tone: 'positive' | 'destructive') =>
  tone === 'positive'
    ? 'border-green-200 bg-green-50 text-green-600 hover:bg-green-100 dark:border-green-900/50 dark:bg-green-950/30 dark:text-green-400 dark:hover:bg-green-950/50'
    : 'border-red-200 bg-red-50 text-red-600 hover:bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400 dark:hover:bg-red-950/50';

export const UserStatusActions = ({ userId, status, size = 'sm' }: UserStatusActionsProps) => {
  const { t } = useTranslation('superAdmin');
  const { showToast } = useToast();
  const currentUser = useAuthStore((s) => s.user);
  const mutation = useChangeUserStatus();

  const actions = getStatusActions(status);
  if (actions.length === 0) {
    return <span className="text-xs text-slate-400">{t('users.actions.none')}</span>;
  }

  // Alternate path 5ب: the super admin cannot change their own account's status.
  const isSelf = currentUser?.id === userId;
  if (isSelf) {
    return <span className="text-xs text-slate-400">{t('users.actions.selfDisabled')}</span>;
  }

  const runAction = (action: UserStatusAction) => {
    const meta = ACTION_META[action];
    if (meta.destructive && !window.confirm(t(`users.actions.confirm.${action}`))) {
      return;
    }

    mutation.mutate(
      { userId, action },
      {
        onSuccess: (updated) => {
          showToast({
            type: 'success',
            title: t('users.actions.successTitle'),
            message: t('users.actions.success', { status: updated.status }),
          });
        },
        onError: (error: unknown) => {
          const detail = (error as { response?: { data?: { detail?: string } } })?.response
            ?.data?.detail;
          showToast({
            type: 'error',
            title: t('users.actions.errorTitle'),
            message: detail || t('users.actions.error'),
          });
        },
      },
    );
  };

  const sizeClasses = size === 'sm' ? 'px-2.5 py-1.5 text-xs' : 'px-3 py-2 text-sm';

  return (
    <div className="flex flex-wrap items-center justify-center gap-2">
      {actions.map((action) => {
        const meta = ACTION_META[action];
        const Icon = meta.icon;
        return (
          <button
            key={action}
            type="button"
            disabled={mutation.isPending}
            onClick={(e) => {
              e.stopPropagation();
              runAction(action);
            }}
            className={`inline-flex items-center gap-1.5 rounded-md border font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${sizeClasses} ${toneClasses(
              meta.tone,
            )}`}
          >
            <Icon size={size === 'sm' ? 14 : 16} />
            {t(`users.actions.${action.toLowerCase()}`)}
          </button>
        );
      })}
    </div>
  );
};
