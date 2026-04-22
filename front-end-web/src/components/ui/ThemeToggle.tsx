import { Moon, Sun, Monitor } from 'lucide-react';
import { useThemeStore } from '../../store/useThemeStore';

export const ThemeToggle = () => {
  const { theme, setTheme } = useThemeStore();

  return (
    <div className="fixed top-4 right-4 flex items-center bg-white dark:bg-slate-900 rounded-full p-1 shadow-md border border-slate-200 dark:border-slate-800">
      <button
        onClick={() => setTheme('light')}
        title="Light Mode"
        className={`p-2 rounded-full transition-colors ${
          theme === 'light' ? 'bg-slate-100 dark:bg-slate-800 text-violet-600 dark:text-violet-400 shadow-sm' : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        <Sun size={18} />
      </button>
      <button
        onClick={() => setTheme('system')}
        title="System Preference"
        className={`p-2 rounded-full transition-colors ${
          theme === 'system' ? 'bg-slate-100 dark:bg-slate-800 text-violet-600 dark:text-violet-400 shadow-sm' : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        <Monitor size={18} />
      </button>
      <button
        onClick={() => setTheme('dark')}
        title="Dark Mode"
        className={`p-2 rounded-full transition-colors ${
          theme === 'dark' ? 'bg-slate-100 dark:bg-slate-800 text-violet-600 dark:text-violet-400 shadow-sm' : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        <Moon size={18} />
      </button>
    </div>
  );
};