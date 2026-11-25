import type { AttemptRow } from './attempt-summary';

export interface AttemptGroup {
  executionKey: string;
  attempts: AttemptRow[];
  preHealingFailure: AttemptRow | null;
}

/** Only an observed failure at or before a candidate is a pre-healing failure. */
export function preHealingFailureFor(sortedAttempts: readonly AttemptRow[]): AttemptRow | null {
  let failure: AttemptRow | null = null;
  for (const attempt of sortedAttempts) {
    if (!failure && ['failed', 'timed_out', 'error'].includes(attempt.normalisedStatus)) {
      failure = attempt;
    }
    if (attempt.healingCandidate && failure) return failure;
  }
  return null;
}

/** Preserve key arrival order and every attempt; never derive a verdict or flaky label. */
export function groupAttempts(rows: readonly AttemptRow[]): AttemptGroup[] {
  const byKey = new Map<string, AttemptRow[]>();
  for (const row of rows) {
    const attempts = byKey.get(row.executionKey);
    if (attempts) attempts.push(row);
    else byKey.set(row.executionKey, [row]);
  }
  return [...byKey].map(([executionKey, attempts]) => {
    attempts.sort((a, b) => a.attemptNo - b.attemptNo);
    const preHealingFailure = preHealingFailureFor(attempts);
    if (preHealingFailure) {
      const index = attempts.indexOf(preHealingFailure);
      attempts.splice(index, 1);
      attempts.unshift(preHealingFailure);
    }
    return { executionKey, attempts, preHealingFailure };
  });
}
