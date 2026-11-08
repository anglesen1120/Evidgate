// 01.6.1-T5.1 + 09.1.1-T1.2 (00 §5.6 W9, AC2): the web E2E workspace. The single `chromium`
// project runs against the BUILT Angular bundle in `apps/web/dist/browser`, served by the
// dependency-free `e2e/static-server.mjs` — not `ng serve`, which would exercise the dev
// build (stale dist and a dev-only bundle would never fail this gate). Retries only on CI,
// trace retained on failure (plan §5, §9): the trace is what makes a red CI job diagnosable
// without re-running it, and `junit` feeds the Evidgate dogfooding of 01.10.1-T1.1.
import { randomInt } from 'node:crypto';
import { defineConfig } from '@playwright/test';

import { E2E_PORT_MAX, E2E_PORT_MIN, INFRA_TAG } from './e2e/infra-tag';

// The webServer port is picked per run, below the OS ephemeral range (see e2e/infra-tag.ts):
// lanes run this suite concurrently on one machine, so a fixed port (4173 before, 4200/8080/8765
// elsewhere) lets one lane's server answer another lane's run, and a port inside the ephemeral
// range can be held by a Docker or SSH forward. Playwright evaluates this file more than once (the
// runner, then every worker process) and the port must not change between those evaluations, so
// the first evaluation stores it in `process.env`; each forked worker inherits that environment,
// and `e2e/static-server.mjs` reads the same variable instead of a hard-coded default.
// `EVIDGATE_WEB_E2E_PORT` may be preset to pin a port deliberately.
const PORT_ENV = 'EVIDGATE_WEB_E2E_PORT';
const presetPort = Number.parseInt(process.env[PORT_ENV] ?? '', 10);
const PORT =
  Number.isInteger(presetPort) && presetPort > 0
    ? presetPort
    : randomInt(E2E_PORT_MIN, E2E_PORT_MAX);
process.env[PORT_ENV] = String(PORT);

const baseURL = `http://127.0.0.1:${PORT}`;

export default defineConfig({
  testDir: './e2e',
  // 09.1.1-T4.4: `e2e/shell/**` is the shell's real-browser suite and runs against the FIXTURES
  // build (`playwright.fixtures.config.ts`, `npm run e2e:fixtures`): it needs the dev fixture
  // backend, a trimmed grant set and the dev-only `/dev/ui-gallery` route, none of which exists in
  // the production bundle this config serves. Excluding it here is what keeps `npm run e2e` — the
  // production smoke + static-server gate — exactly the suite it was.
  testIgnore: ['shell/**', 'dev-login/**', 'core/**'],
  // This workspace has no other `*.test.ts`; keeping the explicit glob documents that the
  // Angular component specs (`src/**/*.spec.ts`) belong to `ng test` / Vitest, not to
  // Playwright, and that `exclude` of the `test` target in angular.json keeps them apart.
  testMatch: '**/*.spec.ts',
  forbidOnly: Boolean(process.env['CI']),
  // No blanket retries: a product assertion that passes on a second try must not turn green. Only
  // a test titled with `@infra` (browser or runner flake) is retried, by `chromium-infra`.
  retries: 0,
  reporter: [
    ['list'],
    ['html', { open: 'never' }],
    // 01.10.1-T1.1 / T48 dogfooding reads this XML; `test-results/` is git-ignored.
    ['junit', { outputFile: 'test-results/e2e-junit.xml' }],
  ],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', grepInvert: INFRA_TAG, use: { browserName: 'chromium' } },
    {
      name: 'chromium-infra',
      grep: INFRA_TAG,
      retries: process.env['CI'] ? 2 : 0,
      use: { browserName: 'chromium' },
    },
  ],
  webServer: {
    // No port argument and no build step here: the server reads `EVIDGATE_WEB_E2E_PORT`
    // above, refuses to start without a port, and exits with a "run npm run build -w
    // apps/web" message when `dist/browser/index.html` is missing (stderr, which Playwright
    // pipes through, so a stale dist is a loud failure rather than a 30 s timeout).
    command: 'node e2e/static-server.mjs',
    url: `${baseURL}/`,
    // Never reuse a process that already answers on this port: the gate must serve the bundle
    // this run's `dist/browser` holds, and a server left over from an earlier build is exactly
    // the stale-bundle failure this workspace exists to catch.
    reuseExistingServer: false,
    timeout: 30_000,
  },
});
