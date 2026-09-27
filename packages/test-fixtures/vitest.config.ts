// 01.6.1-T3.2: @evidgate/test-fixtures workspace overrides on top of the shared
// base config, mirroring packages/cli. The fixture-loader specs are ordinary
// `src/**/*.test.ts` files and run under these defaults; the corpus itself lives
// at the repository root (fixtures/), reached via import.meta.url.
import { defineConfig, mergeConfig } from 'vitest/config';

import baseConfig from '../../vitest.config.base.mts';

export default mergeConfig(
  baseConfig,
  defineConfig({
    test: {
      name: '@evidgate/test-fixtures',
    },
  }),
);
