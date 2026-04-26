import { LogOut } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { LanguageToggle } from './LanguageToggle';
import { ThemeToggle } from './ThemeToggle';
import { useAuthStore } from '../../store/useAuthStore';

export const AppControls = () => {
  const { isAuthenticated, logout } = useAuthStore();
  const { t } = useTranslation('common');

  return (
    <div
      dir="ltr"
      className="fixed top-4 right-4 z-50 flex items-center rounded-full border border-slate-200 bg-white p-1 shadow-md dark:border-slate-800 dark:bg-slate-900"
    >
      <LanguageToggle />

      <div className="mx-2 h-6 w-px bg-slate-200 dark:bg-slate-700" />

      <ThemeToggle />

      {isAuthenticated && (
        <>
          <div className="mx-2 h-6 w-px bg-slate-200 dark:bg-slate-700" />
          
          <button
            type="button"
            onClick={logout}
            title={t('buttons.logout')}
            aria-label={t('buttons.logout')}
            className="inline-flex h-10 w-10 items-center justify-center rounded-full text-slate-500 transition-colors hover:text-red-600 dark:hover:text-red-400"
          >
            <LogOut size={18} strokeWidth={2.5} />
          </button>
        </>
      )}
    </div>
  );
};