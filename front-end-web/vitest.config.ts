import { defineConfig } from 'vitest/config';

// Test config kept separate from vite.config.ts so the app build (tsc -b &&
// vite build) never needs the vitest types. esbuild handles the automatic JSX
// runtime (react/jsx-runtime), so components render without a React global.
export default defineConfig({
  esbuild: {
    jsx: 'automatic',
    jsxImportSource: 'react',
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
