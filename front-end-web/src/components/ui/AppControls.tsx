import { LanguageToggle } from './LanguageToggle';
import { ThemeToggle } from './ThemeToggle';

export const AppControls = () => {
  return (
    <div
      dir="ltr"
      className="fixed top-4 right-4 z-50 flex items-center rounded-full border border-slate-200 bg-white p-1 shadow-md dark:border-slate-800 dark:bg-slate-900"
    >
      <LanguageToggle />

      <div className="mx-2 h-6 w-px bg-slate-200 dark:bg-slate-700" />

      <ThemeToggle />
    </div>
  );
};