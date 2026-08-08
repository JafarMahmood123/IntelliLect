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
    // Explicit, because vitest's default pattern also matches `*.spec.ts` anywhere in the
    // project — which would sweep up the Playwright journey in `e2e/` and try to run it in
    // jsdom, where `@playwright/test` does not resolve and the failure names none of that.
    // The unit suite is `src/**/*.test.*` and nothing else.
    include: ['src/**/*.test.{ts,tsx}'],
    // MUST stay above testing-library's asyncUtilTimeout (5s, set in the setup file). Vitest's
    // default is also 5s, so the two budgets expired together: a slow `findBy*` blew the TEST
    // deadline before its own retry deadline, turning "unable to find element" — which names the
    // element — into a bare "Test timed out". Under full-suite load that is the difference between
    // a diagnosable failure and a mystery.
    testTimeout: 15_000,
    coverage: {
      provider: 'v8',
      // text for the terminal, html to browse a file, lcov for any tool that wants to merge it.
      reporter: ['text', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // Report on every source file, not only the ones a test happened to import — otherwise an
      // entirely untested feature raises the percentage by being invisible to it.
      all: true,
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        // Type-only: erased at build, so there is nothing to execute or cover.
        'src/**/types/**',
        'src/**/*.d.ts',
        // The test harness itself.
        'src/test/**',
        'src/**/*.test.{ts,tsx}',
        // Entry points and generated/config surface: wiring, not logic. Covering these measures
        // that the app starts, which every other test already depends on.
        'src/main.tsx',
        'src/vite-env.d.ts',
        'src/**/index.ts',
      ],
      // Deliberately NOT gated on a threshold yet: the 85% target in docs/work-plan.md is a goal
      // to climb to, and a failing gate on day one just gets switched off. Add thresholds here
      // once the baseline is above them.
    },
  },
});
