import { useTranslation } from 'react-i18next';
import { Button } from '../components/ui/Button';
import { useAuthStore } from '../store/useAuthStore';

export const PendingApprovalPage = () => {
  const { t } = useTranslation('common');
  const logout = useAuthStore((state) => state.logout);

  return (
    <div className="flex min-h-screen items-center justify-center p-4">
      <div className="w-full max-w-md rounded-lg border bg-white p-8 text-center shadow-lg dark:border-gray-800 dark:bg-gray-900">
        <h1 className="mb-2 text-2xl font-bold text-slate-900 dark:text-white">
          {t('pendingApproval.title')}
        </h1>

        <p className="mb-6 text-slate-600 dark:text-slate-400">
          {t('pendingApproval.description')}
        </p>

        <Button type="button" variant="secondary" onClick={logout}>
          {t('buttons.logout')}
        </Button>
      </div>
    </div>
  );
};