// Independent QA for 04.3.2-T1.1: hostile real files and parser boundaries.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { loadSidecarIndex, SIDECAR_FILE_NAME } from './sidecar.js';

const ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const valid = `version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: ${ID}\n`;
const directories: string[] = [];

function fixture(content: string | Uint8Array): { file: string; manifest: string } {
  const directory = mkdtempSync(join(tmpdir(), 'evidgate-sidecar-qa-'));
  directories.push(directory);
  const file = join(directory, 'a.spec.ts');
  const manifest = join(directory, SIDECAR_FILE_NAME);
  writeFileSync(file, '// browserless test\n');
  writeFileSync(manifest, content);
  return { file, manifest };
}

afterEach(() => {
  for (const directory of directories.splice(0))
    rmSync(directory, { recursive: true, force: true });
});

describe('independent sidecar QA', () => {
  it.each([
    ['deep nesting', `version: 1\ncases: ${'['.repeat(100)}0${']'.repeat(100)}\n`],
    ['many aliases', `version: 1\ncases: &a [${Array(3000).fill('*a').join(',')}]\n`],
    ['duplicate root key', `${valid}version: 1\n`],
    ['duplicate case id key', `${valid}    evidgate.id: ${ID}\n`],
    ['root __proto__', `${valid}__proto__: {polluted: true}\n`],
    ['entry __proto__', `${valid}    __proto__: {polluted: true}\n`],
    ['nested __proto__', `${valid}    name: {__proto__: {polluted: true}}\n`],
  ])('refuses %s without mapping or prototype pollution', (_label, content) => {
    const { file } = fixture(content);
    const index = loadSidecarIndex([file]);
    expect(index.lookup(file)).toBeUndefined();
    expect(index.warnings.map((warning) => warning.code)).toEqual(['invalid_sidecar']);
    expect(Object.prototype).not.toHaveProperty('polluted');
  });

  it('refuses a 1 MiB plus one byte file without reading its contents', () => {
    const { file } = fixture(' '.repeat(1024 * 1024 + 1));
    let reads = 0;
    const index = loadSidecarIndex([file], {
      readFile: () => {
        reads += 1;
        return Buffer.from(valid);
      },
    });
    expect(reads).toBe(0);
    expect(index.lookup(file)).toBeUndefined();
    expect(index.warnings[0]?.code).toBe('invalid_sidecar');
  });

  it('accepts a UTF-8 BOM without changing the mapping', () => {
    const { file } = fixture(`\uFEFF${valid}`);
    const index = loadSidecarIndex([file]);
    expect(index.lookup(file)).toBe(ID);
    expect(index.warnings).toEqual([]);
  });

  it('refuses malformed UTF-8 rather than mapping a repaired document', () => {
    const { file } = fixture(
      Buffer.concat([
        Buffer.from('version: 1\ncases:\n  - file: a'),
        Buffer.from([0xff]),
        Buffer.from(`.spec.ts\n    evidgate.id: ${ID}\n`),
      ]),
    );
    const repaired = join(dirname(file), 'a\uFFFD.spec.ts');
    writeFileSync(repaired, '// not the file named by the raw sidecar bytes\n');
    const index = loadSidecarIndex([repaired]);
    expect(index.lookup(repaired)).toBeUndefined();
    expect(index.warnings[0]?.code).toBe('invalid_sidecar');
  });

  it.each([5000, 5001])('enforces the %i-entry schema boundary', (count) => {
    const entry = `  - file: a.spec.ts\n    evidgate.id: ${ID}\n`;
    const { file } = fixture(`version: 1\ncases:\n${entry.repeat(count)}`);
    const index = loadSidecarIndex([file]);
    if (count === 5000) {
      expect(index.lookup(file)).toBe(ID);
      expect(index.warnings).toEqual([]);
    } else {
      expect(index.lookup(file)).toBeUndefined();
      expect(index.warnings[0]?.code).toBe('invalid_sidecar');
    }
  });
});
