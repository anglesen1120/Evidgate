// A run whose evidence never reached the spool must fail Playwright's exit code. The
// reporter reports the buffered failure through the documented `onEnd` result, so a caller
// that only logs reporter errors cannot turn the loss into a passing run. This drives the
// real CLI on the fixture project with a spool it cannot write.
import { execFileSync, spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterAll, beforeAll, describe, expect, it } from 'vitest';

const packageDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = join(packageDir, '..', '..');
const fixtureDir = join(packageDir, 'test-fixtures');
const playwrightCli = join(repoRoot, 'node_modules', '@playwright', 'test', 'cli.js');
const tscBin = join(repoRoot, 'node_modules', 'typescript', 'bin', 'tsc');
const RUN_ID = 'run-spool-failure';

let tempRoot = '';
let spool = '';
let exitCode: number | null = null;
let output = '';

beforeAll(() => {
  // The fixture resolves the package by name, so the compiled entry must be current.
  execFileSync(process.execPath, [tscBin, '-b'], { cwd: packageDir, stdio: 'pipe' });
  tempRoot = mkdtempSync(join(tmpdir(), 'evidgate-spool-failure-'));
  spool = join(tempRoot, 'spool');
  const outputDir = join(tempRoot, 'out');
  mkdirSync(spool);
  mkdirSync(outputDir);
  // A read-only spool root: the reporter cannot create its run directory at onBegin. A uid
  // that ignores mode bits (root, or a filesystem without POSIX modes) would still write, so
  // probe once and fall back to the documented byte cap, which fails the same callback.
  chmodSync(spool, 0o500);
  const fallback: Record<string, string> = {};
  const probe = join(spool, 'write-probe');
  try {
    writeFileSync(probe, '');
    unlinkSync(probe);
    fallback['EVIDGATE_SPOOL_MAX_BYTES'] = '1';
  } catch {
    // Mode bits are enforced: the read-only directory is the failure under test.
  }
  const child = spawnSync(
    process.execPath,
    [playwrightCli, 'test', '--config', join(fixtureDir, 'playwright.config.mjs')],
    {
      cwd: fixtureDir,
      env: {
        ...process.env,
        EVIDGATE_SPOOL_DIR: spool,
        EVIDGATE_RUN_ID: RUN_ID,
        EVIDGATE_E2E_OUTPUT_DIR: outputDir,
        EVIDGATE_TARGET_ENV: 'e2e-local',
        ...fallback,
      },
      encoding: 'utf8',
      timeout: 170_000,
    },
  );
  exitCode = child.status;
  output = `${child.stdout ?? ''}\n${child.stderr ?? ''}`;
}, 180_000);

afterAll(() => {
  if (spool.length > 0) chmodSync(spool, 0o700);
  if (tempRoot.length > 0) rmSync(tempRoot, { recursive: true, force: true });
});

describe('spool failure e2e (real playwright test CLI)', () => {
  it('fails the run when the spool cannot be written', () => {
    expect(exitCode, output).toBe(1);
    expect(output).toContain('[evidgate] reporter failure');
  });

  it('names the cause once without echoing the spool path', () => {
    const lines = output.split('\n').filter((line) => line.startsWith('[evidgate] '));
    expect(lines).toHaveLength(1);
    expect(lines[0]).toContain('reporter failure');
    expect(output).not.toContain(spool);
  });
});
