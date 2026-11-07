// The core-flow suite: login, project switch, run detail, failure triage, evidence viewer and
// onboarding, run against the FIXTURES build (the in-process fixture backend is the seeded tenant).
//
// Why its own config: `playwright.fixtures.config.ts` owns `e2e/shell/**` (the shell's wide
// regression and a11y matrix) and `playwright.config.ts` owns the production smoke. This suite is
// the short, shardable "is the product usable" gate, so it selects only `e2e/core/**` and writes
// its own JUnit file and bundle directory; two suites never serve each other's build.
//
// Shard plan: `npx playwright test -c playwright.core.config.ts --shard=<i>/<n>`; set
// `EVIDGATE_E2E_CORE_SHARD=<i>` to keep each shard's JUnit file apart. Playwright runs the `setup`
// project in every shard before that shard's specs, so each shard reuses one stored auth state
// instead of every spec bootstrapping its own.
import { randomInt } from 'node:crypto';
import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';

import { AUTH_STATE } from './e2e/core/auth-state';

// Ephemeral port: lanes share one machine, and Playwright evaluates this file in the runner and in
// every worker, so the first evaluation pins the port in the environment.
const PORT_ENV = 'EVIDGATE_WEB_FIXTURES_E2E_PORT';
const presetPort = Number.parseInt(process.env[PORT_ENV] ?? '', 10);
const PORT =
  Number.isInteger(presetPort) && presetPort > 0 ? presetPort : randomInt(40_000, 60_000);
process.env[PORT_ENV] = String(PORT);

const baseURL = `http://127.0.0.1:${PORT}`;
// Everything a run writes is keyed on the shard, so shards started together on one machine never
// delete each other's bundle, traces or stored state.
const shard = process.env['EVIDGATE_E2E_CORE_SHARD'];
const suffix = shard === undefined || shard === '' ? '' : `-${shard}`;
const BUNDLE = `dist/core-fixtures${suffix}`;
const CORE_ROOT = resolve(__dirname, BUNDLE, 'browser');
const junitFile = `test-results/e2e-core-junit${suffix}.xml`;

export default defineConfig({
  testDir: './e2e/core',
  forbidOnly: Boolean(process.env['CI']),
  // No blanket retries: a product assertion that passes on a second try must not turn green.
  retries: 0,
  timeout: 60_000,
  // Own artefact directory: Playwright empties it at the start of every run, and the JUnit and
  // auth-state files must survive a second shard run on the same machine.
  outputDir: `test-results/core-artifacts${suffix}`,
  reporter: [
    ['list'],
    ['html', { open: 'never', outputFolder: `test-results/core-report${suffix}` }],
    ['junit', { outputFile: junitFile }],
  ],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'setup',
      testMatch: /.*\.setup\.ts/,
      use: { browserName: 'chromium', channel: 'chrome' },
    },
    {
      name: 'core',
      testMatch: '**/*.spec.ts',
      dependencies: ['setup'],
      use: {
        browserName: 'chromium',
        channel: 'chrome',
        storageState: AUTH_STATE,
      },
    },
  ],
  webServer: {
    command: `npm run build:core:e2e -- --output-path ${BUNDLE} && node e2e/static-server.mjs`,
    url: `${baseURL}/vi/`,
    env: {
      EVIDGATE_WEB_E2E_ROOT: CORE_ROOT,
      EVIDGATE_WEB_E2E_PORT: String(PORT),
      [PORT_ENV]: String(PORT),
    },
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
