import { Moon, Sun, Monitor } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useThemeStore } from '../../store/useThemeStore';

export const ThemeToggle = () => {
  const { theme, setTheme } = useThemeStore();
  const { t } = useTranslation('common');

  return (
    <div className="flex items-center gap-1">
      <button
        type="button"
        onClick={() => setTheme('light')}
        title={t('theme.light')}
        aria-label={t('theme.light')}
        className={`inline-flex h-10 w-10 items-center justify-center rounded-full transition-colors ${
          theme === 'light'
            ? 'bg-slate-100 text-violet-600 shadow-sm dark:bg-slate-800 dark:text-violet-400'
            : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        <Sun size={18} />
      </button>

      <button
        type="button"
        onClick={() => setTheme('system')}
        title={t('theme.system')}
        aria-label={t('theme.system')}
        className={`inline-flex h-10 w-10 items-center justify-center rounded-full transition-colors ${
          theme === 'system'
            ? 'bg-slate-100 text-violet-600 shadow-sm dark:bg-slate-800 dark:text-violet-400'
            : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        <Monitor size={18} />
      </button>

      <button
        type="button"
        onClick={() => setTheme('dark')}
        title={t('theme.dark')}
        aria-label={t('theme.dark')}
        className={`inline-flex h-10 w-10 items-center justify-center rounded-full transition-colors ${
          theme === 'dark'
            ? 'bg-slate-100 text-violet-600 shadow-sm dark:bg-slate-800 dark:text-violet-400'
            : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        <Moon size={18} />
      </button>
    </div>
  );
};