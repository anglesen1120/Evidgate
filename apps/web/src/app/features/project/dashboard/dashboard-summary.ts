import type { RunSummary, WaiverDto } from '@evidgate/sdk';
import type { DashboardRunSample } from '../../../core/data/project-dashboard-data.service';
import { rfc3339InstantMs } from '../../../shared/time/rfc3339-instant';

export function sampleRuns(sample: DashboardRunSample | undefined): readonly RunSummary[] {
  if (!sample) return [];
  return sample.complete ? sample.items.slice(-100) : sample.items;
}

export function runCounts(rows: readonly RunSummary[], canReadDecisions: boolean) {
  return rows.reduce(
    (counts, row) => {
      counts.total++;
      if (row.state === 'collecting') counts.collecting++;
      if (canReadDecisions && row.decision === 'PASS') counts.pass++;
      else if (canReadDecisions && row.decision === 'FAIL') counts.fail++;
      else if (canReadDecisions && row.decision === 'INCONCLUSIVE') counts.inconclusive++;
      else counts.pending++;
      return counts;
    },
    { total: 0, pass: 0, fail: 0, inconclusive: 0, pending: 0, collecting: 0 },
  );
}

export function decisionsByDay(rows: readonly RunSummary[], canReadDecisions: boolean) {
  const days = new Map<string, RunSummary[]>();
  for (const row of rows) {
    const instant = rfc3339InstantMs(row.created_at);
    if (instant === null) continue;
    const day = row.created_at.endsWith('Z')
      ? row.created_at.slice(0, 10)
      : new Date(instant).toISOString().slice(0, 10);
    const group = days.get(day) ?? [];
    group.push(row);
    days.set(day, group);
  }
  return [...days]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([day, runs]) => ({ day, ...runCounts(runs, canReadDecisions) }));
}

export function expiringWaivers(rows: readonly WaiverDto[], now: number): number {
  const end = now + 7 * 24 * 60 * 60 * 1000;
  return rows.filter(
    (row) =>
      row.state === 'approved' &&
      Date.parse(row.expires_at) > now &&
      Date.parse(row.expires_at) <= end,
  ).length;
}

export function recentFailures(rows: readonly RunSummary[], now: number): number {
  const start = now - 7 * 24 * 60 * 60 * 1000;
  return rows.filter(
    (row) =>
      row.decision === 'FAIL' &&
      Date.parse(row.created_at) >= start &&
      Date.parse(row.created_at) <= now,
  ).length;
}
