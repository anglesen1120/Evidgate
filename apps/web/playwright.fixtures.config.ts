// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC4/AC5/AC6; 00 §5.6 W9): the real-browser gate for the app
// shell, run against the FIXTURES build of `apps/web`.
//
// Why a second Playwright config instead of more cases in `playwright.config.ts`: that workspace
// serves the PRODUCTION bundle (`dist/browser`, built by `e2e` → `npm run build`) and a suite that
// needs the dev-only fixture backend — a limited grant set, a 401, a 503, the `/dev/ui-gallery`
// route — cannot run against it. The production config's `testIgnore` keeps `e2e/shell/**` out of
// that run, and this one owns it.
//
// The bundle is built here, into `dist/fixtures` (not `dist/browser`), so the two suites never
// fight over one output directory and neither can serve the other's build:
//   - `ng build --configuration fixtures --localize=true` — the fixtures configuration swaps
//     `dev-backend.ts`/`dev-routes.ts` for their dev replacements (fileReplacements), and the
//     explicit `--localize=true` overrides the configuration's single `vi` locale so BOTH bundles
//     (`/en/`, `/vi/`) exist and the axe matrix can cover each locale. The builder emits one
//     directory per locale, each with its own `<base href="/<locale>/">`.
//   - the same `e2e/static-server.mjs` the production suite uses serves it, pointed at the built
//     root through `EVIDGATE_WEB_E2E_ROOT`; the server is not modified and its SPA fallback is what
//     makes a deep link such as `/vi/orgs/<id>/projects/<id>/overview` answer with the locale's
//     index.html.
//
// Chromium is launched through `channel: 'chrome'`: the bundled download is not part of this
// workspace's provisioning, and a system Chrome is what the design audits use, so the browser the
// checks describe is the browser they run on. Axe runs in that real engine too (no jsdom shortcut),
// which is what makes the colour-contrast rule meaningful.
import { randomInt } from 'node:crypto';
import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';

import { E2E_PORT_MAX, E2E_PORT_MIN } from './e2e/infra-tag';

// Port below the OS ephemeral range, for the same reason the production config picks a
// per-run one: lanes run on one machine concurrently, so a fixed port (4173/4200/8080/8765)
// would let one lane's server answer another lane's run. Playwright evaluates this file once per
// process, so the first evaluation pins the port in the environment and the forked workers and
// `e2e/static-server.mjs` read the same value.
const PORT_ENV = 'EVIDGATE_WEB_FIXTURES_E2E_PORT';
const presetPort = Number.parseInt(process.env[PORT_ENV] ?? '', 10);
const PORT =
  Number.isInteger(presetPort) && presetPort > 0
    ? presetPort
    : randomInt(E2E_PORT_MIN, E2E_PORT_MAX);
process.env[PORT_ENV] = String(PORT);

const baseURL = `http://127.0.0.1:${PORT}`;

/** The served root: the builder's `browser/` output, which holds `en/` and `vi/`. */
const FIXTURES_ROOT = resolve(__dirname, 'dist/fixtures/browser');

export default defineConfig({
  // Only the shell suite: `e2e/static-server*.spec.ts` and `e2e/smoke.spec.ts` belong to the
  // production gate, and `*.spec.ts` under this directory is the whole of this one.
  testDir: './e2e/shell',
  testMatch: '**/*.spec.ts',
  forbidOnly: Boolean(process.env['CI']),
  retries: process.env['CI'] ? 2 : 0,
  // Build, axe and the 360px layouts are fast; a hung navigation fails instead of eating the gate.
  timeout: 60_000,
  reporter: [
    ['list'],
    // Separate artefacts from the production run's, so a fixtures failure cannot overwrite the
    // report + JUnit XML the 01.10.1-T1.1 dogfooding reads from `playwright-report/` and
    // `test-results/e2e-junit.xml`.
    ['html', { open: 'never', outputFolder: 'playwright-report-fixtures' }],
    ['junit', { outputFile: 'test-results/e2e-fixtures-junit.xml' }],
  ],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // The static server now sends the app-origin CSP (09.1.1-T10: nonce, Trusted Types), which
    // refuses the inline script `axeScan` injects. CSP itself is covered by `e2e/csp.spec.ts` on
    // the production suite; this suite drives the shell, so the policy is not enforced here.
    bypassCSP: true,
  },
  projects: [
    {
      name: 'chrome',
      use: { browserName: 'chromium', channel: 'chrome' },
    },
  ],
  webServer: {
    // Build then serve, in that order: `ng build` writes `dist/fixtures/browser` (and deletes that
    // output path first), and the static server refuses to start when the root holds no bundle, so
    // a build failure is a loud start-up failure rather than a suite served an older build.
    command: 'npm run build:fixtures:e2e && node e2e/static-server.mjs',
    // The localized root answers `/` with a 302 to `/vi/`; waiting on a locale directory is
    // therefore also proof that the build emitted both locale bundles.
    url: `${baseURL}/vi/`,
    env: {
      // The static server reads the directory instead of its `../dist/browser` default; the
      // production build directory is never touched by this suite.
      EVIDGATE_WEB_E2E_ROOT: FIXTURES_ROOT,
      // The server's own port variable (it reads this one, or `process.argv[2]`): the server is
      // shared with the production suite and is not modified by this config.
      EVIDGATE_WEB_E2E_PORT: String(PORT),
      // This suite's variable, read by the specs that open their own browser context
      // (`fixturesBaseUrl()` in `e2e/shell/support.ts`) and forwarded to the workers, which do not
      // receive `webServer.env`.
      [PORT_ENV]: String(PORT),
    },
    // Never adopt a server left over from another run: the gate must serve the bundle it just
    // built.
    reuseExistingServer: false,
    // A cold Angular build plus a server start; far above the observed few seconds.
    timeout: 180_000,
  },
});
