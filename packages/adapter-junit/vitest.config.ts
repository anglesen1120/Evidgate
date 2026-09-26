// 01.6.1-T1.2: @evidgate/adapter-junit workspace overrides on top of the shared base config.
import { defineConfig, mergeConfig } from 'vitest/config';

import baseConfig from '../../vitest.config.base.mts';

export default mergeConfig(
  baseConfig,
  defineConfig({
    test: {
      name: '@evidgate/adapter-junit',
    },
  }),
);
