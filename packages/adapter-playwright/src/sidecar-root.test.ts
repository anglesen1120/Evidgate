// 04.3.3-T1 (Note from #32): the sidecar lookup root is pinned to the Playwright
// `config.rootDir`, so every shard reads the same manifests and maps the same `case_id`
// for a test, whatever subset of test files it happens to run.
//
// Without the pin the loader searched from each test file's directory up to the common
// ancestor of *that shard's* files (04.3.2 §10 "Notes (04.3.2-T1, S11 review outcomes)"
// items 2-3). A shard that runs only `other/deep` derives a deeper ancestor than one that
// runs the whole repository, so a manifest above that ancestor could be read by one shard
// and missed by the other — two different identities for the same test.
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { loadSidecarIndex, SIDECAR_FILE_NAME, type SidecarFs } from './sidecar.js';

const UUID_ROOT = '11111111-1111-4111-8111-111111111111';
const UUID_NESTED = '22222222-2222-4222-8222-222222222222';

const tempDirs: string[] = [];

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-sidecar-root-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

/** Creates a test file (and its directories) and returns its absolute path. */
function testFile(path: string): string {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, '// test\n', 'utf8');
  return path;
}

function sidecar(directory: string, lines: string): void {
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, SIDECAR_FILE_NAME), `version: 1\ncases:\n${lines}`, 'utf8');
}

describe('sidecar lookup root (04.3.3-T1, #32)', () => {
  it('maps the same case_id for a test whether the shard runs one file or all files', () => {
    const root = tempDir();
    // The manifest lives at the repository root and maps a nested test file.
    sidecar(root, `  - file: tests/a.spec.ts\n    evidgate.id: ${UUID_ROOT}\n`);
    const a = testFile(join(root, 'tests', 'a.spec.ts'));
    const b = testFile(join(root, 'tests', 'b.spec.ts'));
    const deep = testFile(join(root, 'other', 'deep', 'c.spec.ts'));

    const full = loadSidecarIndex([a, b, deep], undefined, { rootDir: root });
    // A shard that runs only the nested directory: the common ancestor of its files is
    // `…/other/deep`, below the root, so only the pin can reach the manifest above it.
    const deepShard = loadSidecarIndex([deep], undefined, { rootDir: root });
    // A shard that runs only one top-level file.
    const aShard = loadSidecarIndex([a], undefined, { rootDir: root });

    expect(full.lookup(a)).toBe(UUID_ROOT);
    expect(aShard.lookup(a)).toBe(UUID_ROOT);
    expect(deepShard.lookup(a)).toBe(UUID_ROOT);
    // One identity for the same test in every shard: no per-shard `case_id`.
    expect(new Set([full.lookup(a), aShard.lookup(a), deepShard.lookup(a)]).size).toBe(1);
  });

  it('reads a nested manifest regardless of which shard owns its files', () => {
    const root = tempDir();
    const tests = join(root, 'packages', 'a', 'tests');
    sidecar(tests, `  - file: a.spec.ts\n    evidgate.id: ${UUID_NESTED}\n`);
    const a = testFile(join(tests, 'a.spec.ts'));
    const b = testFile(join(root, 'packages', 'b', 'tests', 'b.spec.ts'));

    // `a` alone and `a`+`b` see the same nested manifest.
    expect(loadSidecarIndex([a], undefined, { rootDir: root }).lookup(a)).toBe(UUID_NESTED);
    expect(loadSidecarIndex([a, b], undefined, { rootDir: root }).lookup(a)).toBe(UUID_NESTED);
  });

  it('never reads a manifest above the pinned root', () => {
    const parent = tempDir();
    // A manifest outside the run's root maps nothing for this run.
    sidecar(parent, `  - file: repo/tests/a.spec.ts\n    evidgate.id: ${UUID_ROOT}\n`);
    const root = join(parent, 'repo');
    const a = testFile(join(root, 'tests', 'a.spec.ts'));

    const index = loadSidecarIndex([a], undefined, { rootDir: root });
    expect(index.lookup(a)).toBeUndefined();
    // The outside manifest was never opened: no mapping and no warning about it.
    expect(index.warnings).toEqual([]);
  });

  it('keeps a test file outside the root to its own directory (no walk above the root)', () => {
    const parent = tempDir();
    const root = join(parent, 'repo');
    mkdirSync(root, { recursive: true });
    // A stray test file outside rootDir, with a manifest one level above it that would map it.
    const stray = testFile(join(parent, 'stray', 'z.spec.ts'));
    sidecar(parent, `  - file: stray/z.spec.ts\n    evidgate.id: ${UUID_ROOT}\n`);

    expect(loadSidecarIndex([stray], undefined, { rootDir: root }).lookup(stray)).toBeUndefined();
  });

  it('keeps the symlink refusal with a pinned root', () => {
    const root = tempDir();
    const real = join(root, 'real');
    mkdirSync(real, { recursive: true });
    writeFileSync(
      join(root, 'sidecar-target.yaml'),
      `version: 1\ncases:\n  - file: tests/a.spec.ts\n    evidgate.id: ${UUID_ROOT}\n`,
      'utf8',
    );
    // A sidecar path that is a symlink is refused (never followed), root or no root.
    symlinkSync(join(root, 'sidecar-target.yaml'), join(real, SIDECAR_FILE_NAME));
    const a = testFile(join(real, 'tests', 'a.spec.ts'));

    const index = loadSidecarIndex([a], undefined, { rootDir: root });
    expect(index.lookup(a)).toBeUndefined();
    expect(index.warnings.map((warning) => warning.code)).toEqual(['invalid_sidecar']);
  });

  it('keeps the historical common-ancestor derivation when no root is given', () => {
    const root = tempDir();
    sidecar(root, `  - file: tests/a.spec.ts\n    evidgate.id: ${UUID_ROOT}\n`);
    const a = testFile(join(root, 'tests', 'a.spec.ts'));
    // Two files in sibling directories put the run's common ancestor at the root, so the
    // root manifest is reachable without a pin — the 04.3.2 behaviour, unchanged.
    const b = testFile(join(root, 'other', 'b.spec.ts'));

    expect(loadSidecarIndex([a, b]).lookup(a)).toBe(UUID_ROOT);
    // One file alone derives its own directory as the ancestor: no pin, no manifest above it.
    expect(loadSidecarIndex([a]).lookup(a)).toBeUndefined();
  });

  it('reads each manifest at most once with a pinned root', () => {
    const root = tempDir();
    sidecar(root, `  - file: tests/a.spec.ts\n    evidgate.id: ${UUID_ROOT}\n`);
    const a = testFile(join(root, 'tests', 'a.spec.ts'));
    const b = testFile(join(root, 'tests', 'b.spec.ts'));
    let reads = 0;
    const countingFs: SidecarFs = {
      readFile: (path, maxBytes) => {
        reads += 1;
        try {
          const bytes = readFileSync(path);
          return bytes.byteLength > maxBytes ? bytes.subarray(0, maxBytes + 1) : bytes;
        } catch {
          return undefined;
        }
      },
    };

    loadSidecarIndex([a, b], countingFs, { rootDir: root });
    // One read: `root/evidgate.cases.yaml` once, never per test file. The absent
    // `root/tests/evidgate.cases.yaml` is lstat'ed and not read.
    expect(reads).toBe(1);
  });
});
