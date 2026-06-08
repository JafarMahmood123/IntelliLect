import { useTranslation } from 'react-i18next';

export const InteractionSidebar = () => {
  const { t } = useTranslation('streaming');

  return (
    <aside
      className="flex h-full min-h-[12rem] flex-col border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900 lg:min-h-0 lg:w-80 lg:shrink-0"
      aria-label={t('sidebar.ariaLabel')}
    >
      <p className="text-sm text-slate-600 dark:text-slate-400">{t('sidebar.placeholder')}</p>
    </aside>
  );
};
