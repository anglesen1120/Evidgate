// 01.6.1-T1.2: @evidgate/web workspace overrides on top of the shared base config.
// The Angular application (09.1.1-T2) switches `environment` to 'jsdom' and adds the
// component `*.spec.ts` globs once jsdom is a workspace dependency; Playwright E2E specs
// (01.6.1-T5, apps/web/e2e) stay out by the base `include`.
import { defineConfig, mergeConfig } from 'vitest/config';

import baseConfig from '../../vitest.config.base.mts';

export default mergeConfig(
  baseConfig,
  defineConfig({
    test: {
      name: '@evidgate/web',
    },
  }),
);
