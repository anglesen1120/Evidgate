// 01.6.1-T1.2: @evidgate/cli workspace overrides on top of the shared base config.
// The canonical-hashing specs of 01.3.2-T4 are ordinary `src/**/*.test.ts` files and run
// under these defaults.
import { defineConfig, mergeConfig } from 'vitest/config';

import baseConfig from '../../vitest.config.base.mts';

export default mergeConfig(
  baseConfig,
  defineConfig({
    test: {
      name: '@evidgate/cli',
    },
  }),
);
