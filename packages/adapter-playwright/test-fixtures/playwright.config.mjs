// 04.3.1-T1.3: the tiny browserless fixture project reporter.e2e.test.ts drives with the
// real `playwright test` CLI. The reporter is listed by its package name (AC1) and
// resolved through the workspace symlink; `spool_dir` comes from the option and `run_id`
// from the EVIDGATE_RUN_ID env fallback.
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  workers: 1,
  fullyParallel: false,
  reporter: [
    [
      '@evidgate/playwright-reporter',
      { spool_dir: process.env.EVIDGATE_SPOOL_DIR, producer_id: 'pw-e2e-0' },
    ],
  ],
  outputDir: process.env.EVIDGATE_E2E_OUTPUT_DIR ?? './test-results',
});
