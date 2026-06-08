import { useEffect } from 'react';
import { useThemeStore } from '../../store/useThemeStore';

export const ThemeProvider = ({ children }: { children: React.ReactNode }) => {
  const { theme } = useThemeStore();

  useEffect(() => {
    const root = window.document.documentElement;

    const applyTheme = (isDark: boolean) => {
      root.classList.remove('light', 'dark');
      root.classList.add(isDark ? 'dark' : 'light');
    };

    if (theme === 'system') {
      const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
      
      // 1. Apply the initial system theme
      applyTheme(mediaQuery.matches);

      // 2. Listen for live OS theme changes
      const handleChange = (e: MediaQueryListEvent) => {
        applyTheme(e.matches);
      };
      
      mediaQuery.addEventListener('change', handleChange);

      // 3. Clean up the listener when the user switches away from 'system'
      return () => mediaQuery.removeEventListener('change', handleChange);
    } else {
      // If the user explicitly chose 'dark' or 'light'
      applyTheme(theme === 'dark');
    }
  }, [theme]);

  return <>{children}</>;
};