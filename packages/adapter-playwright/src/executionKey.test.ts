// Cross-runtime parity: fixtures/execution-key/vectors.json is asserted here and by the server
// (tests/Tenancy.IntegrationTests/ExecutionKeyTests.cs), so a layout drift on either side
// fails that side's suite.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { computeExecutionKey } from './executionKey.js';

interface KeyVector {
  readonly name: string;
  readonly case_id: string;
  readonly variant_hash: string;
  readonly target_env: string;
  readonly expect: string;
}

const vectors = JSON.parse(
  readFileSync(
    resolve(
      dirname(fileURLToPath(import.meta.url)),
      '../../../fixtures/execution-key/vectors.json',
    ),
    'utf8',
  ),
) as KeyVector[];

describe('computeExecutionKey', () => {
  it.each(vectors.map((vector) => [vector.name, vector] as const))(
    'vector %s matches the shared fixture',
    (_name, vector) => {
      expect(computeExecutionKey(vector.case_id, vector.variant_hash, vector.target_env)).toBe(
        vector.expect,
      );
    },
  );

  it('keeps a shifted field boundary apart', () => {
    const id = '0198f3c2-7a41-7c9e-8b1d-2f6a5e4d3c10';
    expect(computeExecutionKey(id, 'ab', 'c')).not.toBe(computeExecutionKey(id, 'a', 'bc'));
  });
});
