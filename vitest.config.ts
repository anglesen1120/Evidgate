// 01.6.1-T1.2: `npx vitest run` at the repository root must apply the same conventions as
// the gate (`npm run test --workspaces`). Without this file Vitest falls back to its own
// defaults, whose include glob also matches the Playwright E2E `*.spec.ts` files of
// 01.6.1-T5 (apps/web/e2e) — a root run would then try to execute Playwright specs.
// 09.1.1-T1.1: `apps/web` is no longer a Vitest project here — its Angular component specs
// run through `ng test` (`@angular/build:unit-test`, jsdom, zoneless TestBed), which is what
// `npm run test --workspaces` reaches via apps/web's own `test` script. Collecting them in
// this root run as well would execute them outside the Angular builder's setup.
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    projects: ['packages/*'],
  },
});
