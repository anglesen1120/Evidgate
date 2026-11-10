import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

import type { FullConfig } from '@playwright/test';

// Records the environment names the suite process actually receives, so
// run-fork-context.sh proves the caller's variables were not inherited instead of
// assuming it. Playwright removes the output directory before global setup runs,
// so the file cannot be a leftover from an earlier run.
export default function recordSuiteEnvironment(config: FullConfig): void {
  const resultsDir = join(dirname(config.configFile ?? ''), 'test-results');
  mkdirSync(resultsDir, { recursive: true });
  const names = Object.keys(process.env).sort();
  writeFileSync(join(resultsDir, 'suite-env.json'), `${JSON.stringify({ names }, null, 2)}\n`);
}
