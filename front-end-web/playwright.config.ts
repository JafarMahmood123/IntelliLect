import { defineConfig, devices } from '@playwright/test';

/**
 * Browser-level e2e — work-plan §11.12.
 *
 * Separate from `vitest.config.ts` on purpose: the unit suite runs in jsdom against mocked HTTP
 * and needs nothing running, while this one needs the whole platform. Sharing a runner between
 * the two would make `npm test` depend on Docker, which is how a fast suite stops being run.
 *
 * Scope is one journey, not a second test pyramid. Everything a component test can answer, a
 * component test should answer — they are seconds against minutes, and they fail with the name
 * of the component rather than a screenshot.
 */

const webUrl = process.env.E2E_WEB_URL || 'http://localhost:5173';

export default defineConfig({
  testDir: './e2e',

  // Provisioning alone is a dozen sequential API calls including a session start, which crosses
  // three services. The default 30s would be spent before the browser opened.
  timeout: 120_000,
  expect: { timeout: 15_000 },

  // One worker. Every journey provisions real accounts against one shared platform, and a
  // second worker would be a second live session competing for the same LiveKit and the same
  // embedder — parallelism here buys minutes and costs determinism.
  workers: 1,
  fullyParallel: false,

  // No retries locally: a journey that passes on the second attempt is a flake, and hiding it
  // behind a retry is how a browser suite becomes something nobody trusts. In CI, if this ever
  // runs there, one retry is the compromise that survives a genuinely slow runner.
  retries: process.env.CI ? 1 : 0,
  forbidOnly: Boolean(process.env.CI),

  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
    // Alongside the frontend's existing test-results.xml, so the §10.5 results collector has a
    // single shape to read if this is ever wired into it.
    ['junit', { outputFile: 'playwright-results.xml' }],
  ],

  use: {
    baseURL: webUrl,

    // Kept only for failures. Video and traces on every run fill a disk quickly and nobody
    // opens the passing ones.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',

    // The app requests camera and microphone the moment <LiveKitRoom> mounts. Without these the
    // permission prompt blocks, and the failure surfaces as an unrelated timeout much later.
    permissions: ['camera', 'microphone'],
  },

  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        launchOptions: {
          args: [
            // Synthetic media so a headless browser can publish. The journey asserts nothing
            // about the media itself; these exist so the attempt does not hang.
            '--use-fake-ui-for-media-stream',
            '--use-fake-device-for-media-stream',
            // LiveKit is reached over ws:// on a LAN address in development, from a page served
            // over http://. Chromium is fine with that combination, but the flag keeps the
            // behaviour stable if the dev server is ever fronted by https.
            '--allow-running-insecure-content',
          ],
        },
      },
    },
    // Firefox and WebKit are deliberately absent. One journey on one engine is what §11.12
    // asks for; adding engines multiplies the slowest suite in the repository to test browser
    // differences this project has no evidence of.
  ],

  // Reuses a dev server if one is already up, which is the normal state on a development
  // machine. `npm run dev` proxies /api and /hubs to nginx, so the browser and the API
  // arrangement talk to the same deployment through one origin.
  webServer: {
    command: 'npm run dev',
    url: webUrl,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
