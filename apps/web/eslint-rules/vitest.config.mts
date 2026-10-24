// 09.1.1-T11.4 (AC9): the `style-tokens` ESLint rule specs.
//
// `apps/web`'s `test` script runs two suites: `ng test` for the Angular component specs
// under `src/**` (jsdom, zoneless TestBed, `@angular/build:unit-test`), and this Vitest
// project for the lint specs. They cannot share a runner: the Angular builder only knows
// `src/**/*.spec.ts` and bundles for the browser, while a rule spec drives the ESLint Node
// API against the real repository config.
//
// The shared base config is imported by its real `.mts` path, exactly like
// `packages/*/vitest.config.ts`, so the repository keeps one set of Vitest conventions
// (node environment, explicit imports, no silent empty run). `root` is pinned to
// `apps/web` so the include glob means the same thing whether vitest is started from
// `apps/web` or from the repository root.
import { fileURLToPath } from 'node:url';

import { defineConfig, mergeConfig } from 'vitest/config';

import baseConfig from '../../../vitest.config.base.mts';

export default mergeConfig(
  baseConfig,
  defineConfig({
    test: {
      name: '@evidgate/web-eslint-rules',
      root: fileURLToPath(new URL('..', import.meta.url)),
      include: ['eslint-rules/**/*.spec.ts'],
    },
  }),
);
