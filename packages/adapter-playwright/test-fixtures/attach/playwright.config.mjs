// 04.3.3-T2.2: the browserless fixture project `attach.e2e.test.ts` drives with the real
// `playwright test` CLI. Unlike the 04.3.1 fixture, this one ATTACHES files, so the reporter's
// attachment pipeline runs end to end: allow-list guard, per-kind cap, the CLI's uploader
// against a local HTTP store, and `artifact_ids` on `test.finished`.
//
// The reporter is listed by its package name (resolved through the workspace symlink, exactly
// as a customer config would). `identity` is injected because the reporter's built-in resolver
// keeps the schema-valid placeholder execution key (04.3.2 decision 4) — the server owns the
// real key (00 §7), so the fixture supplies the one this run would have been planned with.
import { defineConfig } from '@playwright/test';

/** The planned execution key the e2e spec's server accepts (`sha256(case ‖ variant ‖ env)`). */
const executionKey = process.env.EVIDGATE_E2E_EXECUTION_KEY;

export default defineConfig({
  testDir: './tests',
  workers: 1,
  fullyParallel: false,
  reporter: [
    [
      '@evidgate/playwright-reporter',
      {
        spool_dir: process.env.EVIDGATE_SPOOL_DIR,
        producer_id: 'pw-attach-0',
        attachment_drain_deadline_ms: 20_000,
        identity: () => ({
          execution_key: executionKey,
          case_id: '99999999-9999-4999-8999-999999999999',
          variant_hash: `sha256:${'12'.repeat(32)}`,
          target_env: 'e2e-attach',
        }),
      },
    ],
  ],
  outputDir: process.env.EVIDGATE_E2E_OUTPUT_DIR ?? './test-results',
});
