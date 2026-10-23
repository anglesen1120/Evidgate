// 04.3.3-T1.2 (AC2, T84): a restarted shard keeps its deterministic producer id and resumes
// its sequence; it never mints a restart suffix and never reuses (run, producer, sequence).
//
// The restart is simulated honestly at the spool boundary: the first writer records a holder
// pid that does not exist, so the second reporter finds the lock stale and takes it over —
// exactly what the writer does after a SIGKILLed Playwright process (the process-exit hook
// cannot run after SIGKILL, so the lock file survives). No real process is killed, so the
// test stays deterministic.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullConfig, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';
import { SHARDS_FILE } from './spool.js';

/** A pid that cannot be a live process, recorded by the "crashed" writer. */
const DEAD_PID = 987_654_321;

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-restart-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fullConfig(total: number, current: number): FullConfig {
  return {
    configFile: '/repo/playwright.config.ts',
    rootDir: '/repo',
    shard: { total, current },
    workers: 1,
    version: '1.63.0',
    metadata: {},
    projects: [],
  } as unknown as FullConfig;
}

function fakeTest(index: number): TestCase {
  return {
    id: `test-${String(index)}`,
    title: `case ${String(index)}`,
    location: { file: `tests/case-${String(index)}.spec.ts`, line: 1, column: 1 },
    annotations: [
      {
        type: 'evidgate.id',
        description: `${(index + 1).toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`,
      },
    ],
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as TestCase;
}

function fakeResult(): TestResult {
  return { status: 'passed', duration: 5, retry: 0, steps: [] } as unknown as TestResult;
}

function suiteOf(tests: TestCase[]): Suite {
  return { allTests: () => tests } as unknown as Suite;
}

function fullResult(): { status: 'passed'; startTime: Date; duration: number } {
  return { status: 'passed', startTime: new Date(), duration: 10 };
}

function readEnvelopes(spool: string, runId: string, producerId: string): EventEnvelope[] {
  const dir = join(spool, runId, producerId);
  const envelopes: EventEnvelope[] = [];
  for (const file of readdirSync(dir)
    .filter((entry) => entry.endsWith('.jsonl'))
    .sort()) {
    for (const line of readFileSync(join(dir, file), 'utf8').split('\n')) {
      if (line.length > 0) envelopes.push(JSON.parse(line) as EventEnvelope);
    }
  }
  return envelopes;
}

/** Emits `tests` through one reporter instance; returns the reporter without closing it. */
function startRun(
  spool: string,
  runId: string,
  tests: TestCase[],
  lock: { pid?: number } = {},
): EvidgateReporter {
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: runId,
    engineVersion: '1.63.0',
    ...(lock.pid === undefined ? {} : { lock: { pid: lock.pid } }),
  });
  reporter.onBegin(fullConfig(3, 1), suiteOf(tests));
  for (const test of tests) {
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(test, fakeResult());
  }
  return reporter;
}

describe('AC2 (T84): a restarted shard resumes the same producer', () => {
  it('keeps shard-1 and continues its sequence without reuse or a restart suffix', () => {
    const spool = tempSpool();
    const runId = 'run-restart';

    // Process 1: three tests, then "killed" — no onEnd, so no close, no shards.json rewrite:
    // the chunks are the only durable trace and the writer lock is left behind.
    startRun(spool, runId, [fakeTest(0), fakeTest(1), fakeTest(2)], { pid: DEAD_PID });
    const before = readEnvelopes(spool, runId, 'shard-1');
    const beforeMax = Math.max(...before.map((envelope) => envelope.sequence));

    // Process 2: same run, same shard config, same spool. The lock holder pid is dead, so
    // the second writer takes the stale lock over; the producer id comes from the config
    // again (never from a suffix).
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: runId,
      engineVersion: '1.63.0',
      lock: { isProcessAlive: (pid) => pid === process.pid },
    });
    reporter.onBegin(fullConfig(3, 1), suiteOf([fakeTest(3)]));
    reporter.onTestBegin(fakeTest(3), fakeResult());
    reporter.onTestEnd(fakeTest(3), fakeResult());
    reporter.onEnd(fullResult());

    // One producer directory, named for the manifest slot: no `shard-1-restart`, no `-2`.
    expect(readdirSync(join(spool, runId))).toEqual(['shard-1']);

    const after = readEnvelopes(spool, runId, 'shard-1');
    const sequences = after.map((envelope) => envelope.sequence);
    // Every envelope is under the same producer id ...
    expect(new Set(after.map((envelope) => envelope.producer_id))).toEqual(new Set(['shard-1']));
    // ... the resumed run starts strictly after the last durable sequence ...
    expect(sequences.slice(before.length)[0]).toBe(beforeMax + 1);
    // ... the whole stream is 1..m contiguous (no gap, no reuse of a durable line) ...
    expect(sequences).toEqual(Array.from({ length: after.length }, (_, index) => index + 1));
    // ... so (run, producer, sequence) is unique client-side (the server's unique key, 00 §9.4).
    expect(new Set(sequences).size).toBe(sequences.length);
    // The rerun emitted its own capability document and run_begin (it is a new process).
    expect(after.filter((envelope) => envelope.type === 'run.log').length).toBeGreaterThan(
      before.filter((envelope) => envelope.type === 'run.log').length,
    );

    // shards.json records the slot and the next sequence, and says when the restart happened.
    const record = JSON.parse(
      readFileSync(join(spool, runId, 'shard-1', SHARDS_FILE), 'utf8'),
    ) as Record<string, unknown>;
    expect(record).toMatchObject({
      producer_id: 'shard-1',
      shard_index: 1,
      shard_total: 3,
      sequence_next: after.length + 1,
    });
    expect(typeof record.last_restart_at).toBe('string');
    expect(Number.isNaN(Date.parse(String(record.last_restart_at)))).toBe(false);
  });

  it('never invents a producer id outside shard-<index> (a server 422 unknown_producer)', () => {
    const spool = tempSpool();
    const runId = 'run-no-invention';
    const reporter = startRun(spool, runId, [fakeTest(0)]);
    reporter.onEnd(fullResult());

    // The manifest declared three slots; this process owns exactly one of them, by name.
    expect(readdirSync(join(spool, runId))).toEqual(['shard-1']);
    const ids = readEnvelopes(spool, runId, 'shard-1').map((envelope) => envelope.producer_id);
    expect(new Set(ids)).toEqual(new Set(['shard-1']));
  });
});
