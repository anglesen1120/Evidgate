// 04.3.2 §8 performance row: the identity map is built once at onBegin and the variant
// hash is memoised per project, so 5k parametrised tests resolve in well under 500 ms.
// The resolver is exercised directly (no spool I/O) so the measurement is the identity
// work, not the disk.
import { performance } from 'node:perf_hooks';

import type { TestCase } from '@playwright/test/reporter';
import { describe, expect, it } from 'vitest';

import { createIdentityResolver } from './identity.js';

const COUNT = 5000;

const project = {
  name: 'chromium',
  metadata: { evidgate: { params: { browser: 'chromium', mode: 'full', locale: 'en-US' } } },
};

function syntheticTest(index: number): TestCase {
  const caseId = `${index.toString(16).padStart(8, '0')}-0000-0000-0000-000000000000`;
  return {
    id: `test-${index}`,
    title: `parametrised case ${index}`,
    location: { file: `tests/case-${index}.spec.ts`, line: index + 1, column: 1 },
    annotations: [{ type: 'evidgate.id', description: caseId }],
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
});
