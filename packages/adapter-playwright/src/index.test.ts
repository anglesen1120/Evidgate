import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { beforeAll, describe, expect, it } from 'vitest';

import entry, { EvidgateReporter } from './index.js';

const packageDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = join(packageDir, '..', '..');
const tscBin = join(repoRoot, 'node_modules', 'typescript', 'bin', 'tsc');

// 04.3.1-T1.1: the package entry point. Playwright loads a reporter module and uses its
// default export as the reporter class, so the default export must be `EvidgateReporter`
// (AC1). The second case resolves the package by name through package.json `exports`, so a
// broken "type"/"main"/"exports", a missing dist entry or a CommonJS entry is caught here.
// That resolution goes through `exports` -> dist, which is gitignored, so the package is
// compiled first (`tsc -b`, incremental) and the test is independent of a prior build.
describe('@evidgate/playwright-reporter entry point', () => {
  beforeAll(() => {
    execFileSync(process.execPath, [tscBin, '-b'], { cwd: packageDir, stdio: 'pipe' });
  }, 120_000);

  it('default-exports the reporter class Playwright instantiates', () => {
    expect(entry).toBe(EvidgateReporter);
    expect(typeof entry).toBe('function');
  });

  it('resolves through the package name and default-exports the same class', async () => {
    // Dynamic import on purpose: this case exercises the package-name resolution boundary
    // (package.json `exports` -> dist), which a static `./index.js` import cannot.
    const resolved = await import('@evidgate/playwright-reporter');
    expect(typeof resolved.default).toBe('function');
    expect(resolved.default.name).toBe(EvidgateReporter.name);
  });
});
