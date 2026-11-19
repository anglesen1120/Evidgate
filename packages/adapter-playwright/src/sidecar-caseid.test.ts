import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { isCanonicalCaseId } from './sidecar.js';

describe('isCanonicalCaseId shared table', () => {
  it('agrees with every row of the table the CLI validator is tested against', () => {
    const table = JSON.parse(
      readFileSync(
        join(dirname(fileURLToPath(import.meta.url)), '../../cli/src/caseId.table.json'),
        'utf8',
      ),
    ) as { cases: { value: string; accepted: boolean }[] };
    expect(table.cases.length).toBeGreaterThan(10);
    for (const row of table.cases) {
      expect([row.value, isCanonicalCaseId(row.value)]).toEqual([row.value, row.accepted]);
    }
  });
});
