// 04.3.2-T1.1: the sidecar loader behaviour, boundaries and failure modes (task file §9, 04 §8).
// Every case runs against a real temp directory: the loader lstats the manifest itself, so the
// file system is part of the contract (a symlink is refused, a missing file is not a warning).
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';

import { loadSidecarIndex, SIDECAR_FILE_NAME, type SidecarFs } from './sidecar.js';
import { SIDECAR_SCHEMA_V1 } from './sidecarSchema.js';

const isWindows = process.platform === 'win32';
const contractsDir = join(dirname(fileURLToPath(import.meta.url)), '../../../contracts');

const UUID_A = '11111111-1111-1111-1111-111111111111';
const UUID_B = '22222222-2222-2222-2222-222222222222';
const UUID_UPPER = 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE';

const tempDirs: string[] = [];

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-sidecar-'));
  tempDirs.push(dir);
  return dir;
}

function writeTestFile(dir: string, name: string): string {
  const path = join(dir, name);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, '// test\n', 'utf8');
  return path;
}

function writeSidecar(dir: string, content: string): void {
  writeFileSync(join(dir, SIDECAR_FILE_NAME), content, 'utf8');
}

function codes(index: { warnings: readonly { code: string }[] }): string[] {
  return index.warnings.map((warning) => warning.code);
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe('loadSidecarIndex', () => {
  it('maps nothing and warns nothing when the sidecar is absent', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBeUndefined();
    expect(index.warnings).toEqual([]);
  });

  it('refuses YAML that does not parse', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    writeSidecar(dir, 'version: [1\ncases: []\n');

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBeUndefined();
    expect(codes(index)).toEqual(['invalid_sidecar']);
  });

  it('refuses anchors, aliases, merge keys and tags', () => {
    const refusals: Array<[string, string]> = [
      ['anchor', 'version: 1\ncases: &x []\n'],
      ['alias', 'version: 1\ncases: []\nother: *x\n'],
      ['merge key', 'version: 1\ncases: []\nx:\n  <<: {a: 1}\n'],
      ['tag', 'version: !!int 1\ncases: []\n'],
    ];

    for (const [label, content] of refusals) {
      const dir = tempDir();
      const file = writeTestFile(dir, 'a.spec.ts');
      writeSidecar(dir, content);

      const index = loadSidecarIndex([file]);

      expect(index.lookup(file), label).toBeUndefined();
      expect(codes(index), label).toEqual(['invalid_sidecar']);
    }
  });

  it('refuses a sidecar larger than 1 MiB before reading it', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    writeFileSync(join(dir, SIDECAR_FILE_NAME), 'a'.repeat(1024 * 1024 + 1), 'utf8');
    let reads = 0;
    const fs: SidecarFs = {
      readFile: () => {
        reads += 1;
        return 'version: 1\ncases: []\n';
      },
    };

    const index = loadSidecarIndex([file], fs);

    expect(reads).toBe(0);
    expect(codes(index)).toEqual(['invalid_sidecar']);
  });

  it.skipIf(isWindows)('refuses a sidecar that is not a regular file', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    const target = join(tempDir(), 'real.yaml');
    writeFileSync(target, 'version: 1\ncases: []\n', 'utf8');
    symlinkSync(target, join(dir, SIDECAR_FILE_NAME));

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBeUndefined();
    expect(codes(index)).toEqual(['invalid_sidecar']);
  });

  it('refuses a sidecar that breaks the schema, including unknown keys', () => {
    const violations: Array<[string, string]> = [
      ['bad uuid', `version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: nope\n`],
      ['missing id', `version: 1\ncases:\n  - file: a.spec.ts\n`],
      ['extra top-level key', `version: 1\ncases: []\nextra: 1\n`],
      [
        'extra entry key',
        `version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: ${UUID_A}\n    extra: 1\n`,
      ],
    ];

    for (const [label, content] of violations) {
      const dir = tempDir();
      const file = writeTestFile(dir, 'a.spec.ts');
      writeSidecar(dir, content);

      const index = loadSidecarIndex([file]);

      expect(index.lookup(file), label).toBeUndefined();
      expect(codes(index), label).toEqual(['invalid_sidecar']);
    }
  });

  it('refuses entries that name a path outside the manifest directory', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    writeSidecar(
      dir,
      [
        'version: 1',
        'cases:',
        '  - file: ../x.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '  - file: /abs.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '  - file: a/./b.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '',
      ].join('\n'),
    );

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBeUndefined();
    expect(codes(index)).toEqual([
      'invalid_sidecar_entry',
      'invalid_sidecar_entry',
      'invalid_sidecar_entry',
    ]);
  });

  it('holds a file two entries give different ids, and accepts a repeated id', () => {
    const conflicting = tempDir();
    const conflictingFile = writeTestFile(conflicting, 'a.spec.ts');
    writeSidecar(
      conflicting,
      [
        'version: 1',
        'cases:',
        '  - file: a.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '  - file: a.spec.ts',
        `    evidgate.id: ${UUID_B}`,
        '',
      ].join('\n'),
    );

    const held = loadSidecarIndex([conflictingFile]);

    expect(held.lookup(conflictingFile)).toBeUndefined();
    expect(codes(held)).toEqual(['sidecar_conflict']);

    const repeated = tempDir();
    const repeatedFile = writeTestFile(repeated, 'a.spec.ts');
    writeSidecar(
      repeated,
      [
        'version: 1',
        'cases:',
        '  - file: a.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '  - file: a.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '',
      ].join('\n'),
    );

    const mapped = loadSidecarIndex([repeatedFile]);

    expect(mapped.lookup(repeatedFile)).toBe(UUID_A);
    expect(mapped.warnings).toEqual([]);
  });

  it('warns when an entry names a file that is not on disk', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    writeSidecar(
      dir,
      `version: 1\ncases:\n  - file: missing.spec.ts\n    evidgate.id: ${UUID_A}\n`,
    );

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBeUndefined();
    expect(codes(index)).toEqual(['invalid_sidecar_entry']);
  });

  it('treats a __proto__ file name like any other name', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    const proto = writeTestFile(dir, '__proto__');
    writeSidecar(dir, `version: 1\ncases:\n  - file: __proto__\n    evidgate.id: ${UUID_A}\n`);
    const before = Object.getOwnPropertyNames(Object.prototype).length;

    const index = loadSidecarIndex([file, proto]);

    expect(index.lookup(proto)).toBe(UUID_A);
    expect(Object.getOwnPropertyNames(Object.prototype).length).toBe(before);
  });

  it('maps a nested entry relative to the manifest directory', () => {
    const dir = tempDir();
    const a = writeTestFile(dir, 'a.spec.ts');
    const b = writeTestFile(dir, 'sub/b.spec.ts');
    writeSidecar(
      dir,
      [
        'version: 1',
        'cases:',
        '  - file: a.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '  - file: sub/b.spec.ts',
        `    evidgate.id: ${UUID_B}`,
        '',
      ].join('\n'),
    );

    const index = loadSidecarIndex([a, b]);

    expect(index.lookup(a)).toBe(UUID_A);
    expect(index.lookup(b)).toBe(UUID_B);
    expect(index.warnings).toEqual([]);
  });

  it('reads each directory sidecar once', () => {
    const dir = tempDir();
    const a = writeTestFile(dir, 'a.spec.ts');
    const b = writeTestFile(dir, 'b.spec.ts');
    writeSidecar(
      dir,
      [
        'version: 1',
        'cases:',
        '  - file: a.spec.ts',
        `    evidgate.id: ${UUID_A}`,
        '  - file: b.spec.ts',
        `    evidgate.id: ${UUID_B}`,
        '',
      ].join('\n'),
    );
    let reads = 0;
    const fs: SidecarFs = {
      readFile: (path) => {
        reads += 1;
        return readFileSync(path, 'utf8');
      },
    };

    const index = loadSidecarIndex([a, b], fs);

    expect(reads).toBe(1);
    expect(index.lookup(a)).toBe(UUID_A);
    expect(index.lookup(b)).toBe(UUID_B);
  });

  it('returns ids lower-cased', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    writeSidecar(dir, `version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: ${UUID_UPPER}\n`);

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBe(UUID_UPPER.toLowerCase());
  });

  it('parses CRLF line endings', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    writeSidecar(
      dir,
      `version: 1\r\ncases:\r\n  - file: a.spec.ts\r\n    evidgate.id: ${UUID_A}\r\n`,
    );

    const index = loadSidecarIndex([file]);

    expect(index.lookup(file)).toBe(UUID_A);
    expect(index.warnings).toEqual([]);
  });

  it('caps the number of warnings a hostile tree can produce', () => {
    const dir = tempDir();
    const file = writeTestFile(dir, 'a.spec.ts');
    const entries = Array.from(
      { length: 2001 },
      (_, position) => `  - file: missing-${position}.spec.ts\n    evidgate.id: ${UUID_A}`,
    );
    writeSidecar(dir, ['version: 1', 'cases:', ...entries, ''].join('\n'));

    const index = loadSidecarIndex([file]);

    expect(index.warnings.length).toBe(2000);
    expect(codes(index).every((code) => code === 'invalid_sidecar_entry')).toBe(true);
  });
});

describe('sidecarSchema', () => {
  it('matches contracts/catalog/sidecar.v1.json', () => {
    const contract: unknown = JSON.parse(
      readFileSync(join(contractsDir, 'catalog/sidecar.v1.json'), 'utf8'),
    );

    expect(SIDECAR_SCHEMA_V1).toEqual(contract);
  });
});
