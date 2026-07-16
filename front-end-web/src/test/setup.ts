import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';
// Initialize i18n so components render real EN strings in tests.
import '../lib/i18n';

afterEach(() => {
  cleanup();
});
