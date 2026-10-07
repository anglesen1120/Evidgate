// 04.3.2 §8 performance row: the identity map is built once at onBegin and the variant
// hash is memoised per project, so 5k parametrised tests resolve in well under 500 ms.
// Two cases: the resolver alone (no disk) and the real onBegin path — loadSidecarIndex
// over a 5,000-entry manifest plus the resolver over 5,000 sidecar-mapped tests.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';

import type { TestCase } from '@playwright/test/reporter';
import { describe, expect, it } from 'vitest';

import { createIdentityResolver } from './identity.js';
import { loadSidecarIndex, SIDECAR_FILE_NAME } from './sidecar.js';

const COUNT = 5000;

const project = {
  name: 'chromium',
  metadata: { evidgate: { params: { browser: 'chromium', mode: 'full', locale: 'en-US' } } },
};

function caseId(index: number): string {
  // index + 1 so the first id is never the nil UUID (refused by the strict UUID check).
  return `${(index + 1).toString(16).padStart(8, '0')}-0000-0000-0000-000000000000`;
}

function syntheticTest(index: number): TestCase {
  return {
    id: `test-${index}`,
    title: `parametrised case ${index}`,
    location: { file: `tests/case-${index}.spec.ts`, line: index + 1, column: 1 },
    annotations: [{ type: 'evidgate.id', description: caseId(index) }],
    parent: { project: () => project },
  } as unknown as TestCase;
}

function sidecarTest(file: string, index: number): TestCase {
  return {
    id: `test-${index}`,
    title: `case ${index}`,
    location: { file, line: 1, column: 1 },
    annotations: [],
    parent: { project: () => project },
  } as unknown as TestCase;
}

describe('identity resolution performance', () => {
  it('resolves 5k parametrised tests in under 500 ms', () => {
    const resolver = createIdentityResolver({});
    const tests = Array.from({ length: COUNT }, (_, index) => syntheticTest(index));

    const start = performance.now();
    const identities = tests.map((test) => resolver(test));
    const elapsed = performance.now() - start;

    expect(identities).toHaveLength(COUNT);
    expect(new Set(identities.map((identity) => identity.variant_hash)).size).toBe(1);
    expect(elapsed).toBeLessThan(500);
  });

  it('loads a 5,000-entry sidecar and resolves 5,000 tests in under 500 ms', () => {
    const dir = mkdtempSync(join(tmpdir(), 'evidgate-perf-'));
    try {
      const files: string[] = [];
      const entries: string[] = [];
      for (let index = 0; index < COUNT; index += 1) {
        const name = `case-${index}.spec.ts`;
        writeFileSync(join(dir, name), '');
        files.push(join(dir, name));
        entries.push(`  - file: ${name}\n    evidgate.id: ${caseId(index)}\n`);
      }
      writeFileSync(join(dir, SIDECAR_FILE_NAME), `version: 1\ncases:\n${entries.join('')}`);
      const tests = files.map((file, index) => sidecarTest(file, index));

      // Timed: the real onBegin identity work — loadSidecarIndex (one manifest read, YAML
      // parse, Ajv, 5,000 existsSync) plus the resolver over 5,000 tests. Not timed: the
      // 5,000 fixture files, and no spool I/O (the reporter's per-test flush is outside
      // this task).
      const start = performance.now();
      const index = loadSidecarIndex(files);
      const resolver = createIdentityResolver({ sidecar: index });
      const identities = tests.map((test) => resolver(test));
      const elapsed = performance.now() - start;

      expect(identities).toHaveLength(COUNT);
      expect(identities.every((identity) => identity.case_id !== undefined)).toBe(true);
      expect(elapsed).toBeLessThan(500);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
