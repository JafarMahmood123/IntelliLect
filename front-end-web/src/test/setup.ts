import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup, configure } from '@testing-library/react';
// Initialize i18n so components render real EN strings in tests.
import '../lib/i18n';

// Testing-library's default async timeout is 1s, which findByRole loses under load: it rebuilds
// the accessibility tree on every retry, so it is far slower than the text queries used elsewhere.
// Whole files pass in isolation and fail in the parallel suite, and WHICH file fails moves as
// tests are added — a flake that reads as a real regression every time. Raised here rather than
// per call site, because the next findByRole added would just reintroduce it.
//
// This only extends how long a query WAITS; a query that resolves immediately is unaffected, and
// a genuinely failing test now takes this long to report instead of 1s.
configure({ asyncUtilTimeout: 5000 });

afterEach(() => {
  cleanup();
});
