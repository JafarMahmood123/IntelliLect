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
    // MUST stay above testing-library's asyncUtilTimeout (5s, set in the setup file). Vitest's
    // default is also 5s, so the two budgets expired together: a slow `findBy*` blew the TEST
    // deadline before its own retry deadline, turning "unable to find element" — which names the
    // element — into a bare "Test timed out". Under full-suite load that is the difference between
    // a diagnosable failure and a mystery.
    testTimeout: 15_000,
  },
});
