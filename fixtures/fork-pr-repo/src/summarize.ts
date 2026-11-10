export type CaseStatus = 'passed' | 'failed' | 'skipped';

export interface CaseResult {
  readonly name: string;
  readonly status: CaseStatus;
  readonly durationMs: number;
}

export interface RunSummary {
  readonly tests: number;
  readonly failed: number;
  readonly skipped: number;
  readonly durationMs: number;
}

// Refuses a negative duration instead of summing it, so a producer that
// reports a sentinel value fails the suite rather than skewing the totals.
export function summarize(results: readonly CaseResult[]): RunSummary {
  let failed = 0;
  let skipped = 0;
  let durationMs = 0;
  for (const result of results) {
    if (!Number.isFinite(result.durationMs) || result.durationMs < 0) {
      throw new RangeError(`negative duration for ${result.name}`);
    }
    if (result.status === 'failed') failed += 1;
    if (result.status === 'skipped') skipped += 1;
    durationMs += result.durationMs;
  }
  return { tests: results.length, failed, skipped, durationMs };
}
