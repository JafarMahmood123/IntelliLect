import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Button } from '../components/ui/Button';
import { PageHeader } from '../components/ui/PageHeader';
import { StatusBadge } from '../components/ui/StatusBadge';
import { useAuthStore } from '../store/useAuthStore';

export const DashboardPage = () => {
  const { t } = useTranslation('common');
  const { user, logout } = useAuthStore();

  if (!user) {
    return null;
  }

  return (
    <div className="mx-auto w-full max-w-4xl p-6">
      <PageHeader
        title={t('dashboard.welcome', { name: user.firstName })}
        description={t('dashboard.description')}
      />

      <div className="max-w-2xl rounded-lg border bg-white p-6 shadow-lg dark:border-gray-800 dark:bg-gray-900">
        <div className="space-y-4 text-slate-700 dark:text-slate-300">
          <p>
            <strong>{t('dashboard.email')}:</strong> {user.email}
          </p>

          <p>
            <strong>{t('dashboard.role')}:</strong> {user.roleName}
          </p>

          <div className="flex items-center gap-2">
            <strong>{t('dashboard.status')}:</strong>
            <StatusBadge status={user.status} />
          </div>
        </div>

        <div className="mt-6 flex flex-wrap gap-4">
          <Button type="button" variant="danger" onClick={logout}>
            {t('buttons.logout')}
          </Button>

          {user.roleName === 'SuperAdmin' && (
            <Link
              to="/super-admin"
              className="inline-flex items-center justify-center rounded-lg bg-gradient-to-r from-violet-600 to-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-md transition-all hover:from-violet-700 hover:to-indigo-700 hover:shadow-lg"
            >
              {t('dashboard.openSuperAdmin')}
            </Link>
          )}
        </div>
      </div>
    </div>
  );
};