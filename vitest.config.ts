// 01.6.1-T1.2: `npx vitest run` at the repository root must apply the same conventions as
// the gate (`npm run test --workspaces`). Without this file Vitest falls back to its own
// defaults, whose include glob also matches the Playwright E2E `*.spec.ts` files of
// 01.6.1-T5 (apps/web/e2e) — a root run would then try to execute Playwright specs.
// The projects bring their own workspace config, which extends vitest.config.base.mts.
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    projects: ['apps/web', 'packages/*'],
  },
});
