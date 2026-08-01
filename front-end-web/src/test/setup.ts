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

// jsdom implements neither of the two browser APIs the whiteboard draws with. Both are stubbed
// here rather than mocked per test file, because a component that merely CONTAINS a canvas would
// otherwise fail — and the failure ("Not implemented: HTMLCanvasElement.prototype.getContext")
// points at jsdom rather than at the test.
//
// These are deliberately inert. The whiteboard's real logic — fitting the canvas to the video,
// the wire format, the eraser — lives in pure modules with their own tests precisely so that
// nothing worth asserting depends on a painted pixel.
if (!('ResizeObserver' in globalThis)) {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
}

if (typeof HTMLCanvasElement !== 'undefined') {
  HTMLCanvasElement.prototype.getContext = (() =>
    new Proxy(
      { canvas: null, save: () => {}, restore: () => {} },
      // Every 2D context call is a no-op that returns undefined, and every property reads as a
      // writable no-op. A Proxy rather than a hand-written stub so a newly used canvas method
      // never breaks an unrelated test.
      { get: (target, prop) => (prop in target ? Reflect.get(target, prop) : () => {}), set: () => true },
    )) as unknown as HTMLCanvasElement['getContext'];
}

afterEach(() => {
  cleanup();
});
