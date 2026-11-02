import { resolve } from 'node:path';
import { defineConfig } from '@playwright/test';

import production from './playwright.config';

export default defineConfig(production, {
  testDir: './e2e/dev-login',
  testIgnore: [],
  use: {
    // Request bodies contain an ephemeral login secret; never retain them in trace artefacts.
    trace: 'off',
    screenshot: 'off',
    bypassCSP: true,
  },
  webServer: {
    command:
      'npx ng build --configuration development --localize=true --output-path dist/dev-login && node e2e/static-server.mjs',
    url: `http://127.0.0.1:${process.env['EVIDGATE_WEB_E2E_PORT']}/vi/`,
    env: {
      EVIDGATE_WEB_E2E_ROOT: resolve(__dirname, 'dist/dev-login/browser'),
    },
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
