import { Moon, Sun, Monitor } from 'lucide-react';
import { useThemeStore } from '../../store/useThemeStore';

export const ThemeToggle = () => {
  const { theme, setTheme } = useThemeStore();

  return (
    <div className="fixed top-4 right-4 flex items-center bg-gray-200 dark:bg-gray-800 rounded-lg p-1 shadow-sm border dark:border-gray-700">
      <button
        onClick={() => setTheme('light')}
        title="Light Mode"
        className={`p-2 rounded-md transition-colors ${
          theme === 'light' ? 'bg-white dark:bg-gray-600 text-purple-600 dark:text-purple-400 shadow-sm' : 'text-gray-500 hover:text-gray-900 dark:hover:text-gray-300'
        }`}
      >
        <Sun size={18} />
      </button>
      <button
        onClick={() => setTheme('system')}
        title="System Preference"
        className={`p-2 rounded-md transition-colors ${
          theme === 'system' ? 'bg-white dark:bg-gray-600 text-purple-600 dark:text-purple-400 shadow-sm' : 'text-gray-500 hover:text-gray-900 dark:hover:text-gray-300'
        }`}
      >
        <Monitor size={18} />
      </button>
      <button
        onClick={() => setTheme('dark')}
        title="Dark Mode"
        className={`p-2 rounded-md transition-colors ${
          theme === 'dark' ? 'bg-white dark:bg-gray-600 text-purple-600 dark:text-purple-400 shadow-sm' : 'text-gray-500 hover:text-gray-900 dark:hover:text-gray-300'
        }`}
      >
        <Moon size={18} />
      </button>
    </div>
  );
};