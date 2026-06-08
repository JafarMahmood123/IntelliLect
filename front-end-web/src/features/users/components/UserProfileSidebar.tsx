import type { ReactNode } from 'react';
import { CalendarDays, KeyRound, Mail, Shield, UserCircle, UserRound } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { StatusBadge } from '../../../components/ui/StatusBadge';
import type { User } from '../../../types';

export type UserProfileSection = 'profile' | 'password';

interface UserProfileSidebarProps {
  user: User;
  activeSection: UserProfileSection;
  onSectionChange: (section: UserProfileSection) => void;
}

interface ActionItem {
  id: UserProfileSection;
  label: string;
  description: string;
  icon: ReactNode;
}

const formatDateTime = (value: string) => {
  const date = new Date(value);

  return `${date.toLocaleDateString()} ${date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
  })}`;
};

export const UserProfileSidebar = ({
  user,
  activeSection,
  onSectionChange,
}: UserProfileSidebarProps) => {
  const { t } = useTranslation('users');

  const getRoleLabel = (roleName: string) => {
    const key = `roles.${roleName}`;
    const translated = t(key);

    return translated === key ? roleName : translated;
  };

  const actions: ActionItem[] = [
    {
      id: 'profile',
      label: t('navigation.profileInfo'),
      description: t('navigation.profileInfoDescription'),
      icon: <UserRound size={18} />,
    },
    {
      id: 'password',
      label: t('navigation.changePassword'),
      description: t('navigation.changePasswordDescription'),
      icon: <KeyRound size={18} />,
    },
  ];

  return (
    <aside className="space-y-6 lg:sticky lg:top-24 lg:self-start">
      <section className="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col items-center text-center">
          <div className="flex h-20 w-20 items-center justify-center rounded-2xl bg-violet-100 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">
            <UserCircle size={44} />
          </div>

          <h2 className="mt-4 text-lg font-semibold text-slate-900 dark:text-white">
            {user.firstName} {user.lastName}
          </h2>

          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            @{user.userName}
          </p>

          <div className="mt-4">
            <StatusBadge status={user.status} />
          </div>
        </div>

        <div className="mt-6 space-y-4 border-t border-slate-200 pt-6 dark:border-slate-800">
          <div className="flex gap-3 text-sm">
            <Mail size={18} className="mt-0.5 shrink-0 text-slate-400" />
            <div>
              <p className="font-medium text-slate-700 dark:text-slate-300">
                {t('summary.email')}
              </p>
              <p className="break-words text-slate-500 dark:text-slate-400">
                {user.email}
              </p>
            </div>
          </div>

          <div className="flex gap-3 text-sm">
            <Shield size={18} className="mt-0.5 shrink-0 text-slate-400" />
            <div>
              <p className="font-medium text-slate-700 dark:text-slate-300">
                {t('summary.role')}
              </p>
              <p className="text-slate-500 dark:text-slate-400">
                {getRoleLabel(user.roleName)}
              </p>
            </div>
          </div>

          <div className="flex gap-3 text-sm">
            <CalendarDays size={18} className="mt-0.5 shrink-0 text-slate-400" />
            <div>
              <p className="font-medium text-slate-700 dark:text-slate-300">
                {t('summary.joinedAt')}
              </p>
              <p className="text-slate-500 dark:text-slate-400">
                {formatDateTime(user.createdAtUtc)}
              </p>
            </div>
          </div>
        </div>
      </section>

      <section className="rounded-2xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="px-3 py-2">
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">
            {t('navigation.title')}
          </h3>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
            {t('navigation.description')}
          </p>
        </div>

        <div className="mt-2 space-y-2">
          {actions.map((action) => {
            const isActive = activeSection === action.id;

            return (
              <button
                key={action.id}
                type="button"
                onClick={() => onSectionChange(action.id)}
                className={`flex w-full items-start gap-3 rounded-xl border p-3 text-start transition
                  ${
                    isActive
                      ? 'border-violet-500 bg-violet-50 text-violet-700 dark:border-violet-500/70 dark:bg-violet-950/30 dark:text-violet-300'
                      : 'border-transparent text-slate-600 hover:border-slate-200 hover:bg-slate-50 dark:text-slate-400 dark:hover:border-slate-800 dark:hover:bg-slate-950/60'
                  }`}
              >
                <span
                  className={`mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg
                    ${
                      isActive
                        ? 'bg-violet-100 dark:bg-violet-900/50'
                        : 'bg-slate-100 dark:bg-slate-800'
                    }`}
                >
                  {action.icon}
                </span>

                <span className="min-w-0">
                  <span className="block text-sm font-semibold">
                    {action.label}
                  </span>
                  <span className="mt-0.5 block text-xs text-slate-500 dark:text-slate-400">
                    {action.description}
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      </section>
    </aside>
  );
};