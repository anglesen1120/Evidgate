// The duplicate refusal describes only the offenders it names: a crafted report can repeat one
// megabyte title thousands of times, and the redaction work must be bounded before it starts.
import { describe, expect, it, vi } from 'vitest';

import type * as RedactModule from './redact.js';

vi.mock('./redact.js', async (importOriginal) => {
  const actual = await importOriginal<typeof RedactModule>();
  return { ...actual, redact: vi.fn(actual.redact) };
});

import { CliError } from './errors.js';
import { assertDistinctTests, type EnumeratedTest } from './manifestMapping.js';
import { redact } from './redact.js';

const entry: EnumeratedTest = {
  file: 'tests/a.spec.ts',
  titlePath: ['x'.repeat(5_000)],
  caseId: '12345678-1234-1234-1234-123456789abc',
  variantHash: `sha256:${'a'.repeat(64)}`,
};

describe('assertDistinctTests details bound', () => {
  it('describes_only_the_twenty_named_offenders', () => {
    const mocked = vi.mocked(redact);
    mocked.mockClear();
    const repeated = Array.from({ length: 25 }, () => entry);
    let error: CliError | undefined;
    try {
      assertDistinctTests(repeated);
    } catch (caught) {
      expect(caught).toBeInstanceOf(CliError);
      if (caught instanceof CliError) {
        error = caught;
      }
    }

    expect(error?.details).toHaveLength(21);
    expect(error?.details?.at(-1)).toBe('(5 more)');
    // Describing is the expensive part: three scans per named test (file, titles, assembled),
    // for the twenty named offenders only — never for all twenty-five.
    expect(mocked).toHaveBeenCalledTimes(60);
  });
});
