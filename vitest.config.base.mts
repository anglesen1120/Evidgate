// 01.6.1-T1.2 / 00 §5.6 W9: the one Vitest base every workspace extends
// (packages/cli, packages/sdk, packages/adapter-*).
//
// `apps/web` does NOT extend it: since 09.1.1 its Angular component specs run through the
// Angular builder (`@angular/build:unit-test`, runner vitest, jsdom, zoneless TestBed), so
// the root vitest projects list excludes it and this file keeps its Node conventions.
//
// It is an `.mts` file on purpose: the workspaces import it by its real `.mts` path
// (`tsconfig.spec.json` sets `allowImportingTsExtensions` for that), which both the
// bundled and the Node-native Vitest config loader resolve directly. Node never remaps a
// `.js`/`.mjs` specifier onto the `.mts` source, so those spellings fail under
// `npx vitest run --configLoader native`.
//
// Conventions it fixes:
// - node environment by default; the Angular workspace runs its own jsdom environment
//   inside the `@angular/build:unit-test` builder instead of extending this file;
// - specs are `src/**/*.test.ts`, imported explicitly (`globals: false`), so a spec is
//   never mistaken for the Playwright E2E `*.spec.ts` files of 01.6.1-T5;
// - a workspace with no spec fails instead of silently passing.
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
    passWithNoTests: false,
    // Coverage provider intentionally left unset: the licence row is still ⚠️ pending in
    // docs/adr/0002-compatibility-and-licence-matrix.md (01.1.1-T1). Pinning a provider
    // here before that row exists would add an unreviewed dependency.
  },
});
