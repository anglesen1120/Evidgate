import type { RunSummary, WaiverDto } from '@evidgate/sdk';
import {
  decisionsByDay,
  expiringWaivers,
  recentFailures,
  runCounts,
  sampleRuns,
} from './dashboard-summary';

function run(id: string, decision: RunSummary['decision'] = null): RunSummary {
  return {
    run_id: id,
    decision,
    state: 'collecting',
    created_at: '2026-11-23T09:00:00Z',
    completeness: null,
    parent_run_id: null,
    rerun_scope: null,
    unverified_ref: false,
    ref: {
      kind: null,
      name: null,
      pr_number: null,
      head_sha: null,
      base_sha: null,
      trigger_event: null,
      is_fork: false,
    },
  };
}

describe('Dashboard sampled calculations', () => {
  it('takes the latest tail only after the cursor walk reached the end', () => {
    const rows = Array.from({ length: 101 }, (_, index) => run(String(index)));
    expect(sampleRuns({ items: rows, complete: true })[0]?.run_id).toBe('1');
    expect(sampleRuns({ items: rows, complete: false })[0]?.run_id).toBe('0');
    expect(sampleRuns(undefined)).toEqual([]);
  });
  it('keeps missing decisions neutral and collecting independent from decisions', () => {
    const rows = [run('a', 'PASS'), run('b', 'INCONCLUSIVE'), run('c')];
    expect(runCounts(rows, true)).toEqual({
      total: 3,
      pass: 1,
      fail: 0,
      inconclusive: 1,
      pending: 1,
      collecting: 3,
    });
    expect(runCounts(rows, false).pass).toBe(0);
    expect(runCounts(rows, false).pending).toBe(3);
    expect(decisionsByDay(rows, true)[0]?.day).toBe('2026-11-23');
  });
  it('groups runs sharing a UTC date even when their supplied local dates differ', () => {
    const offsetRun = { ...run('offset', 'FAIL'), created_at: '2026-11-24T02:00:00+07:00' };
    expect(decisionsByDay([run('utc', 'PASS'), offsetRun], true)).toEqual([
      expect.objectContaining({ day: '2026-11-23', pass: 1, fail: 1, total: 2 }),
    ]);
  });
  it('does not normalize impossible failure dates into the seven-day window', () => {
    const created = (day: string) => ({ ...run(day, 'FAIL'), created_at: `${day}T00:00:00Z` });
    expect(
      recentFailures([created('2026-02-30'), created('2026-03-02')], Date.UTC(2026, 2, 3)),
    ).toBe(1);
  });
  it('counts only approved future waiver expiries within seven days including the upper boundary', () => {
    const now = Date.parse('2026-11-23T00:00:00Z');
    const waiver: WaiverDto = {
      id: 'w',
      project_id: 'p',
      scope: 'case',
      target_id: 'c',
      reason: 'Review',
      requested_by: 'u',
      approved_by: 'a',
      state: 'approved',
      expires_at: '2026-11-30T00:00:00Z',
      created_at: '2026-11-20T00:00:00Z',
      updated_at: '2026-11-20T00:00:00Z',
    };
    expect(expiringWaivers([waiver], now)).toBe(1);
    expect(expiringWaivers([{ ...waiver, state: 'requested' }], now)).toBe(0);
    expect(expiringWaivers([{ ...waiver, expires_at: '2026-11-23T00:00:00Z' }], now)).toBe(0);
    expect(expiringWaivers([{ ...waiver, expires_at: '2026-11-30T00:00:01Z' }], now)).toBe(0);
    expect(recentFailures([run('fail', 'FAIL'), run('pending')], now + 86400000)).toBe(1);
  });
});
