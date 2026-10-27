import type { AttemptRow } from './attempt-summary';

export interface AttemptGroup {
  executionKey: string;
  attempts: AttemptRow[];
  preHealingFailure: AttemptRow | null;
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
    const preHealingFailure = attempts.some((attempt) => attempt.healingCandidate)
      ? (attempts.find((attempt) =>
          ['failed', 'timed_out', 'error'].includes(attempt.normalisedStatus),
        ) ?? null)
      : null;
    if (preHealingFailure) {
      const index = attempts.indexOf(preHealingFailure);
      attempts.splice(index, 1);
      attempts.unshift(preHealingFailure);
    }
    return { executionKey, attempts, preHealingFailure };
  });
}

/** At most 50 DOM rows, even for one huge retry chain. Repeat its pinned failure on continuation. */
export function paginateAttemptGroups(groups: readonly AttemptGroup[]): AttemptGroup[][] {
  const pages: AttemptGroup[][] = [];
  let page: AttemptGroup[] = [];
  let count = 0;
  const flush = () => {
    if (page.length) pages.push(page);
    page = [];
    count = 0;
  };
  for (const group of groups) {
    if (group.attempts.length <= 50) {
      if (count + group.attempts.length > 50) flush();
      page.push(group);
      count += group.attempts.length;
    } else {
      flush();
      let offset = 0;
      while (offset < group.attempts.length) {
        const pinned = offset > 0 && group.preHealingFailure ? [group.preHealingFailure] : [];
        const attempts = group.attempts.slice(offset, offset + 50 - pinned.length);
        offset += attempts.length;
        pages.push([{ ...group, attempts: [...pinned, ...attempts] }]);
      }
    }
  }
  flush();
  return pages;
}
