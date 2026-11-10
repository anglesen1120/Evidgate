import { expect, test } from '@playwright/test';

import { summarize } from '../src/summarize';

const CASES = [
  { name: 'checkout', status: 'passed', durationMs: 120 },
  { name: 'search', status: 'failed', durationMs: 80 },
  { name: 'export', status: 'skipped', durationMs: 0 },
] as const;

test.describe('summarize', () => {
  test('counts each status and sums the durations', () => {
    expect(summarize(CASES)).toEqual({ tests: 3, failed: 1, skipped: 1, durationMs: 200 });
  });

  test('returns zeroes for a run with no results', () => {
    expect(summarize([])).toEqual({ tests: 0, failed: 0, skipped: 0, durationMs: 0 });
  });

  test('refuses a negative duration', () => {
    const negative = [{ name: 'checkout', status: 'passed', durationMs: -1 }] as const;
    expect(() => summarize(negative)).toThrow(/negative duration/);
  });
});
