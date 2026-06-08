import { useTranslation } from 'react-i18next';

export const LanguageToggle = () => {
  const { t, i18n } = useTranslation('common');

  const currentLanguage = i18n.resolvedLanguage?.startsWith('ar') ? 'ar' : 'en';

  return (
    <div className="flex items-center gap-1">
      <button
        type="button"
        onClick={() => void i18n.changeLanguage('en')}
        title={t('language.switchToEnglish')}
        aria-label={t('language.switchToEnglish')}
        className={`inline-flex h-10 w-12 items-center justify-center rounded-full text-xs font-semibold transition-colors ${
          currentLanguage === 'en'
            ? 'bg-slate-100 text-violet-600 shadow-sm dark:bg-slate-800 dark:text-violet-400'
            : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        EN
      </button>

      <button
        type="button"
        onClick={() => void i18n.changeLanguage('ar')}
        title={t('language.switchToArabic')}
        aria-label={t('language.switchToArabic')}
        className={`inline-flex h-10 w-12 items-center justify-center rounded-full text-xs font-semibold transition-colors ${
          currentLanguage === 'ar'
            ? 'bg-slate-100 text-violet-600 shadow-sm dark:bg-slate-800 dark:text-violet-400'
            : 'text-slate-500 hover:text-slate-900 dark:hover:text-slate-300'
        }`}
      >
        AR
      </button>
    </div>
  );
};