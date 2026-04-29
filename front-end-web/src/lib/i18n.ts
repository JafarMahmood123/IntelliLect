import i18n from 'i18next';
import LanguageDetector from 'i18next-browser-languagedetector';
import { initReactI18next } from 'react-i18next';

import enCommon from '../locales/en/common.json';
import enAuth from '../locales/en/auth.json';
import enAdmin from '../locales/en/admin.json';
import enSuperAdmin from '../locales/en/superAdmin.json';
import enUsers from '../locales/en/users.json';

import arCommon from '../locales/ar/common.json';
import arAuth from '../locales/ar/auth.json';
import arAdmin from '../locales/ar/admin.json';
import arSuperAdmin from '../locales/ar/superAdmin.json';
import arUsers from '../locales/ar/users.json';

const resolveLanguage = (language?: string) => {
  return language?.startsWith('ar') ? 'ar' : 'en';
};

const applyDocumentLanguage = (language?: string) => {
  if (typeof document === 'undefined') {
    return;
  }

  const resolvedLanguage = resolveLanguage(language);

  document.documentElement.lang = resolvedLanguage;
  document.documentElement.dir = resolvedLanguage === 'ar' ? 'rtl' : 'ltr';
};

void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      en: {
        common: enCommon,
        auth: enAuth,
        admin: enAdmin,
        superAdmin: enSuperAdmin,
        users: enUsers,
      },
      ar: {
        common: arCommon,
        auth: arAuth,
        admin: arAdmin,
        superAdmin: arSuperAdmin,
        users: arUsers,
      },
    },
    fallbackLng: 'en',
    supportedLngs: ['en', 'ar'],
    ns: ['common', 'auth', 'admin', 'superAdmin', 'users'],
    defaultNS: 'common',
    load: 'languageOnly',
    detection: {
      order: ['localStorage', 'navigator', 'htmlTag'],
      caches: ['localStorage'],
    },
    interpolation: {
      escapeValue: false,
    },
    returnNull: false,
  });

applyDocumentLanguage(i18n.resolvedLanguage ?? i18n.language);
i18n.on('languageChanged', applyDocumentLanguage);

export default i18n;