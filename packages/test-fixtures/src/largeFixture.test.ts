// The deterministic large-report fixture: the repo keeps the generator and a loader-shaped
// directory (meta.json, input.json = generator parameters, expected.json = pinned checksums),
// never the report itself. These specs load that directory through the shared loader, run
// the generator from its parameters through the command line and compare the bytes.
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  appendFileSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';

import { loadFixture } from './loadFixture.js';

const REPO_ROOT = fileURLToPath(new URL('../../..', import.meta.url));
const GENERATOR = join(REPO_ROOT, 'scripts/dev/gen-large-report/gen-large-report.mjs');
const FIXTURE_DIR = join(REPO_ROOT, 'fixtures/large/100k-tests');

interface Sized {
  readonly tests: number;
  readonly bytes: number;
  readonly sha256: string;
}
interface Checksums {
  readonly seed: number;
  readonly full: Sized;
  readonly small: Sized;
}
interface Params {
  readonly seed: number;
  readonly tests: number;
  readonly small_tests: number;
}
interface Summary extends Sized {
  readonly seed: number;
}

const fixture = loadFixture(FIXTURE_DIR);
const checksums = fixture.expected as Checksums;
const params = JSON.parse(fixture.inputText) as Params;
const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-large-'));
  tempDirs.push(dir);
  return dir;
}

function generate(outDir: string, tests: number, seed = checksums.seed): Summary {
  const stdout = execFileSync(
    process.execPath,
    [GENERATOR, '--out-dir', outDir, '--tests', String(tests), '--seed', String(seed)],
    { encoding: 'utf8', maxBuffer: 1 << 20 },
  );
  return JSON.parse(stdout) as Summary;
}

function sha256(bytes: Buffer): string {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

/** The loader has no checksum rule; a fixture whose bytes are not stored pins them here. */
function assertChecksum(file: string, expected: string): void {
  const actual = sha256(readFileSync(file));
  if (actual !== expected) throw new Error(`${file}: checksum ${actual} != pinned ${expected}`);
}

describe('gen-large-report', () => {
  it('writes byte-identical output for the same seed and equals the committed small checksum', () => {
    const first = generate(tempDir(), checksums.small.tests);
    const second = generate(tempDir(), checksums.small.tests);

    expect(second).toEqual(first);
    expect(first.sha256).toBe(checksums.small.sha256);
    expect(first.bytes).toBe(checksums.small.bytes);
  });

  it('writes a different report for a different seed', () => {
    const firstDir = tempDir();
    const secondDir = tempDir();
    const first = generate(firstDir, 1_000, 1);
    const second = generate(secondDir, 1_000, 2);
    const testcaseLines = [firstDir, secondDir].map((dir) =>
      readFileSync(join(dir, 'input.xml'), 'utf8')
        .split('\n')
        .filter((line) => line.includes('<testcase ')),
    );

    expect(second.sha256).not.toBe(first.sha256);
    expect(testcaseLines[0]).toHaveLength(1_000);
    expect(testcaseLines[1]).toHaveLength(1_000);
    expect(testcaseLines[1]).not.toEqual(testcaseLines[0]);
  });

  it('generates the 100 000-test report equal to the committed checksum', () => {
    const outDir = tempDir();
    const started = performance.now();
    const summary = generate(outDir, checksums.full.tests);
    const elapsedMs = Math.round(performance.now() - started);

    // One printed line is the measured generation time and size; no budget is asserted here.
    console.log(`large-report 100k: ${elapsedMs} ms, ${summary.bytes} bytes, ${summary.sha256}`);
    expect(summary.sha256).toBe(checksums.full.sha256);
    expect(summary.bytes).toBe(checksums.full.bytes);
    assertChecksum(join(outDir, 'input.xml'), checksums.full.sha256);
  }, 120_000);

  it('declares exactly the cases it writes', () => {
    const outDir = tempDir();
    generate(outDir, 1_000);
    const xml = readFileSync(join(outDir, 'input.xml'), 'utf8');

    expect(xml.match(/<testcase /g)).toHaveLength(1_000);
    expect(xml).toContain('tests="1000"');
    expect(xml).not.toContain('\r');
  });

  it.each([['0'], ['-1'], ['1.5'], ['abc'], ['1000001']])('refuses --tests %s', (tests) => {
    expect(() =>
      execFileSync(process.execPath, [GENERATOR, '--out-dir', tempDir(), `--tests=${tests}`], {
        stdio: 'pipe',
      }),
    ).toThrowError(/--tests must be an integer/);
  });

  it('writes only input.xml into the target directory', () => {
    const outDir = tempDir();
    mkdirSync(join(outDir, 'nested'));
    generate(join(outDir, 'nested'), 10);

    expect(readdirSync(join(outDir, 'nested'))).toEqual(['input.xml']);
    expect(readdirSync(outDir)).toEqual(['nested']);
  });

  it('refuses a symlinked input.xml without changing the outside sentinel', () => {
    const root = tempDir();
    const outDir = join(root, 'out');
    const sentinel = join(root, 'sentinel.xml');
    const original = Buffer.from('outside report sentinel\n');
    mkdirSync(outDir);
    writeFileSync(sentinel, original);
    symlinkSync(sentinel, join(outDir, 'input.xml'));

    const result = spawnSync(process.execPath, [GENERATOR, '--out-dir', outDir, '--tests=10'], {
      encoding: 'utf8',
    });

    expect(result.status).toBe(2);
    expect(result.stderr).toMatch(/input\.xml.*symlink/i);
    expect(readFileSync(sentinel)).toEqual(original);
    expect(readdirSync(outDir)).toEqual(['input.xml']);
  });

  it('refuses a symlinked out-dir without changing its target directory', () => {
    const root = tempDir();
    const target = join(root, 'target');
    const outDir = join(root, 'out');
    const original = Buffer.from('target directory sentinel\n');
    mkdirSync(target);
    writeFileSync(join(target, 'input.xml'), original);
    symlinkSync(target, outDir, 'dir');

    const result = spawnSync(process.execPath, [GENERATOR, '--out-dir', outDir, '--tests=10'], {
      encoding: 'utf8',
    });

    expect(result.status).toBe(2);
    expect(result.stderr).toMatch(/out-dir.*symlink/i);
    expect(readdirSync(target)).toEqual(['input.xml']);
    expect(readFileSync(join(target, 'input.xml'))).toEqual(original);
  });
});

describe('large fixture through loadFixture', () => {
  it('loads from the committed directory with the generator parameters', () => {
    expect(fixture.domain).toBe('large');
    expect(fixture.name).toBe('100k-tests');
    expect(fixture.meta.addedBy).toBe('04.1.3-T3.1');
    expect(params.seed).toBe(checksums.seed);
    expect(params.tests).toBe(checksums.full.tests);
    expect(params.small_tests).toBe(checksums.small.tests);
  });

  it('generates the small report from the parameters and matches its pinned checksum', () => {
    const outDir = tempDir();
    generate(outDir, params.small_tests, params.seed);

    assertChecksum(join(outDir, 'input.xml'), checksums.small.sha256);
  });

  it('refuses a report whose bytes changed', () => {
    const outDir = tempDir();
    generate(outDir, params.small_tests, params.seed);
    appendFileSync(join(outDir, 'input.xml'), ' ');

    expect(() => assertChecksum(join(outDir, 'input.xml'), checksums.small.sha256)).toThrowError(
      /checksum sha256:[0-9a-f]{64} != pinned/,
    );
  });
});
