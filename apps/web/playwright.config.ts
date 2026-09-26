// 01.6.1-T5.1 (00 §5.6 W9): the web E2E workspace. Angular screens land in 09.1.1-T2, so the
// single `chromium` project points at the static page served by `e2e/static-server.mjs`
// instead of `ng serve`. Retries only on CI, trace retained on failure (plan §5, §9) — the
// trace is what makes a red CI job diagnosable without re-running it.
import { defineConfig } from '@playwright/test';

const PORT = 4173;
const baseURL = `http://127.0.0.1:${PORT}`;

export default defineConfig({
  testDir: './e2e',
  // Vitest owns `src/**/*.test.ts` (vitest.config.base.mts); the E2E specs are `*.spec.ts`
  // and are never collected by the Vitest projects of the root vitest.config.ts.
  testMatch: '**/*.spec.ts',
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: {
    command: `node e2e/static-server.mjs ${PORT}`,
    url: `${baseURL}/`,
    // Locally a running server is reused (faster re-runs); on CI the job must serve its
    // own static page, so a stale process would hide a broken command.
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },
});
