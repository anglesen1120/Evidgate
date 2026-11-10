import { defineConfig } from '@playwright/test';

// The suite never opens a page: every test calls plain TypeScript. A fork pull
// request therefore needs neither a browser download nor a network, and the
// report is produced from Node alone.
export default defineConfig({
  testDir: './tests',
  outputDir: './test-results',
  globalSetup: './suite-env.global-setup.ts',
  fullyParallel: true,
  workers: 1,
  retries: 0,
  reporter: [
    ['list'],
    ['json', { outputFile: 'test-results/report.json' }],
    ['html', { open: 'never' }],
  ],
});
