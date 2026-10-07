// 01.6.1-T1.2: @evidgate/playwright-reporter workspace overrides on top of the shared
// base config. Playwright itself runs the E2E specs in apps/web (01.6.1-T5), never
// Vitest; this package's reporter.e2e.test.ts drives the real `playwright test` CLI as a
// child process instead.
import { defineConfig, mergeConfig } from 'vitest/config';

import baseConfig from '../../vitest.config.base.mts';

export default mergeConfig(
  baseConfig,
  defineConfig({
    test: {
      name: '@evidgate/playwright-reporter',
    },
  }),
);
