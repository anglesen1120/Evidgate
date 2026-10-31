// 04.3.3-T1.1 (AC1): shard → producer mapping with real reporter types.
//
// The facts this file pins come from the installed @playwright/test 1.63.0, not from a
// guess: the reporter's `onBegin(config, suite)` receives `FullConfig.shard`
// (`{ total, current }`, `current` one-based — node_modules/playwright/types/test.d.ts:2149-2159),
// set by `--shard <current>/<total>` (node_modules/playwright/lib/program.js:216) or a
// config `shard:` (lib/common/index.js:528); there is no `PLAYWRIGHT_SHARD` environment
// variable (grep SHARD over node_modules/playwright/lib finds only PWTEST_SHARD_WEIGHTS).
// The derived ids are the manifest slots the server registered — `shard-<index>`, 1-based,
// `shard-1` unsharded (plans/features/README.md T54; ProducerRegistry.cs:344).
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullConfig, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';
import { isMergeReplay, isProducerId, PRODUCER_ID_PATTERN, resolveProducerId } from './shard.js';
import { EvidgateInvalidShardError, SHARDS_FILE } from './spool.js';

const tempDirs: string[] = [];
const ORIGINAL_SHARD_ENV = process.env.PLAYWRIGHT_SHARD;

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-shard-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
  if (ORIGINAL_SHARD_ENV === undefined) delete process.env.PLAYWRIGHT_SHARD;
  else process.env.PLAYWRIGHT_SHARD = ORIGINAL_SHARD_ENV;
});

/** A resolved Playwright config; only `shard`/`rootDir` are read by the reporter. */
function fullConfig(shard: { total: number; current: number } | null): FullConfig {
  return {
    configFile: '/repo/playwright.config.ts',
    rootDir: '/repo',
    shard,
    workers: 1,
    version: '1.63.0',
    metadata: {},
    projects: [],
  } as unknown as FullConfig;
}

function fakeTest(index: number, project = 'chromium'): TestCase {
  return {
    id: `test-${String(index)}`,
    title: `case ${String(index)}`,
    location: { file: `tests/case-${String(index)}.spec.ts`, line: 1, column: 1 },
    annotations: [{ type: 'evidgate.id', description: caseUuid(index) }],
    parent: { project: () => ({ name: project }) },
  } as unknown as TestCase;
}

/** A non-nil canonical UUID per index (the identity gate accepts it as `case_id`). */
function caseUuid(index: number): string {
  return `${(index + 1).toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`;
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

function readShardsRecord(
  spool: string,
  runId: string,
  producerId: string,
): Record<string, unknown> {
  return JSON.parse(readFileSync(join(spool, runId, producerId, SHARDS_FILE), 'utf8')) as Record<
    string,
    unknown
  >;
}

describe('resolveProducerId (T54 naming)', () => {
  it('names a sharded run shard-<current>, one-based, and keeps the total', () => {
    expect(resolveProducerId(fullConfig({ total: 4, current: 2 }))).toEqual({
      producer_id: 'shard-2',
      shard_index: 2,
      shard_total: 4,
    });
    // The first shard is `shard-1`, never `shard-0` (README T54 is explicitly 1-based and
    // matches the manifest's `shard-<ordinal>`, CreateRunCommand.cs:666).
    expect(resolveProducerId(fullConfig({ total: 4, current: 1 })).producer_id).toBe('shard-1');
    // The blob reporter's zero-padded file name does not leak into the id.
    expect(resolveProducerId(fullConfig({ total: 10, current: 3 })).producer_id).toBe('shard-3');
  });

  it('names an unsharded run shard-1 and claims no total (T54)', () => {
    expect(resolveProducerId(fullConfig(null))).toEqual({ producer_id: 'shard-1', shard_index: 1 });
    // A reporter invoked without a config (a tool, a unit test) is unsharded too.
    expect(resolveProducerId(undefined)).toEqual({ producer_id: 'shard-1', shard_index: 1 });
    expect(resolveProducerId({})).toEqual({ producer_id: 'shard-1', shard_index: 1 });
  });

  it('derives only spool-safe ids', () => {
    for (const current of [1, 2, 255, 256]) {
      const resolved = resolveProducerId({ shard: { total: 256, current } });
      expect(PRODUCER_ID_PATTERN.test(resolved.producer_id)).toBe(true);
      expect(isProducerId(resolved.producer_id)).toBe(true);
      expect(resolved.producer_id.length).toBeLessThanOrEqual(128);
    }
  });

  it('refuses a shard outside the manifest range instead of deriving an id', () => {
    for (const shard of [
      { total: 0, current: 0 },
      { total: 4, current: 0 },
      { total: 4, current: 5 },
      { total: 257, current: 1 },
      { total: 4, current: 1.5 },
      { total: Number.NaN, current: 1 },
    ]) {
      expect(() => resolveProducerId({ shard })).toThrow(EvidgateInvalidShardError);
    }
    try {
      resolveProducerId({ shard: { total: 4, current: 9 } });
    } catch (error) {
      expect((error as EvidgateInvalidShardError).code).toBe('invalid_shard');
    }
  });

  it('reads no environment variable (PLAYWRIGHT_SHARD does not exist in 1.63)', () => {
    // Pretend a CI template exports the variable the plan §5 guessed at. Playwright 1.63 has
    // no such variable, so honouring it would invent an id outside the manifest's slots.
    process.env.PLAYWRIGHT_SHARD = '2/5';
    expect(resolveProducerId(fullConfig(null))).toEqual({ producer_id: 'shard-1', shard_index: 1 });
    expect(resolveProducerId(fullConfig({ total: 5, current: 2 })).producer_id).toBe('shard-2');
  });
});

describe('isMergeReplay (AC3 detection)', () => {
  it('recognises the merge-reports subcommand only', () => {
    expect(isMergeReplay(['merge-reports', 'blob-report'])).toBe(true);
    expect(isMergeReplay(['merge-reports'])).toBe(true);
    // An option of the merge command itself, after the subcommand (the only valid form:
    // `-c/--config` belongs to `merge-reports`/`test`, not to the root program).
    expect(isMergeReplay(['merge-reports', '--config', 'merge.config.ts', 'blob-report'])).toBe(
      true,
    );
    // A value that happens to be named `merge-reports` is not the subcommand.
    expect(isMergeReplay(['test', '--config', 'merge-reports'])).toBe(false);
    expect(isMergeReplay(['test', '--shard=1/2'])).toBe(false);
    expect(isMergeReplay([])).toBe(false);
    // A test named `merge-reports` passed to `playwright test` is not the subcommand.
    expect(isMergeReplay(['test', 'merge-reports'])).toBe(false);
  });
});

describe('AC1: two shards are two producers with independent contiguous sequences', () => {
  it('emits shard-1 and shard-2 under distinct producer ids, each sequence 1..m', () => {
    const spool = tempSpool();
    const runId = 'run-ac1';
    const shard1Tests = [fakeTest(0), fakeTest(1)];
    const shard2Tests = [fakeTest(2)];

    for (const [config, tests] of [
      [fullConfig({ total: 2, current: 1 }), shard1Tests],
      [fullConfig({ total: 2, current: 2 }), shard2Tests],
    ] as const) {
      const reporter = new EvidgateReporter({
        spool_dir: spool,
        run_id: runId,
        engineVersion: '1.63.0',
      });
      reporter.onBegin(config, suiteOf(tests));
      for (const test of tests) {
        reporter.onTestBegin(test, fakeResult());
        reporter.onTestEnd(test, fakeResult());
      }
      reporter.onEnd(fullResult());
    }

    expect(readdirSync(join(spool, runId)).sort()).toEqual(['shard-1', 'shard-2']);

    const first = readEnvelopes(spool, runId, 'shard-1');
    const second = readEnvelopes(spool, runId, 'shard-2');
    // Each producer's sequences are contiguous from 1 and independent of the other shard.
    expect(first.map((envelope) => envelope.sequence)).toEqual(
      Array.from({ length: first.length }, (_, index) => index + 1),
    );
    expect(second.map((envelope) => envelope.sequence)).toEqual(
      Array.from({ length: second.length }, (_, index) => index + 1),
    );
    // One capability document + run_begin + 2 tests × 2 events + run_end for shard-1 ...
    expect(first).toHaveLength(7);
    // ... and 1 test × 2 events for shard-2, so the union covers both manifest slots.
    expect(second).toHaveLength(5);
    expect(first.at(-1)?.ext).toMatchObject({ marker: 'run_end' });
    expect(second.at(-1)?.ext).toMatchObject({ marker: 'run_end' });
    expect(first.every((envelope) => envelope.producer_id === 'shard-1')).toBe(true);
    expect(second.every((envelope) => envelope.producer_id === 'shard-2')).toBe(true);
    // The shard record names the manifest slot each producer stands for (§4).
    expect(readShardsRecord(spool, runId, 'shard-1')).toMatchObject({
      producer_id: 'shard-1',
      shard_index: 1,
      shard_total: 2,
      sequence_next: first.length + 1,
    });
    expect(readShardsRecord(spool, runId, 'shard-2')).toMatchObject({
      producer_id: 'shard-2',
      shard_index: 2,
      shard_total: 2,
    });
  });

  it('records the unsharded slot (shard-1) with no total', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-unsharded',
      engineVersion: '1.63.0',
    });
    reporter.onBegin(fullConfig(null), suiteOf([fakeTest(0)]));
    reporter.onTestBegin(fakeTest(0), fakeResult());
    reporter.onTestEnd(fakeTest(0), fakeResult());
    reporter.onEnd(fullResult());

    const record = readShardsRecord(spool, 'run-unsharded', 'shard-1');
    expect(record).toMatchObject({ producer_id: 'shard-1', shard_index: 1 });
    expect(record.shard_total).toBeUndefined();
  });

  it('honours an explicit producer_id that equals the derived shard id', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-explicit-match',
      producer_id: 'shard-2',
      engineVersion: '1.63.0',
    });
    reporter.onBegin(fullConfig({ total: 3, current: 2 }), suiteOf([fakeTest(0)]));
    reporter.onTestBegin(fakeTest(0), fakeResult());
    reporter.onTestEnd(fakeTest(0), fakeResult());
    reporter.onEnd(fullResult());

    expect(readdirSync(join(spool, 'run-explicit-match'))).toEqual(['shard-2']);
    expect(readShardsRecord(spool, 'run-explicit-match', 'shard-2')).toMatchObject({
      producer_id: 'shard-2',
      shard_index: 2,
      shard_total: 3,
    });
  });

  it('refuses an explicit producer_id that disagrees with the shard, before any envelope', async () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-explicit-mismatch',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      reporter.onBegin(fullConfig({ total: 2, current: 1 }), suiteOf([fakeTest(0)]));

      // onBegin buffers (the callbacks never throw); onEnd reports the refusal and nothing
      // was written: a wrong id would be a server 422 `unknown_producer` or a slot collision.
      await expect(reporter.onEnd(fullResult())).resolves.toEqual({ status: 'failed' });
      const line = String(write.mock.calls[0]?.[0]);
      expect(line).toContain('[evidgate] reporter failure');
      expect(line).toContain('producer_id_shard_conflict');
      expect(readdirSync(spool)).toEqual([]);
    } finally {
      write.mockRestore();
    }
  });

  it('keeps an explicit producer_id on an unsharded run (04.3.1 compatibility)', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-legacy',
      producer_id: 'pw-legacy-0',
      engineVersion: '1.63.0',
    });
    reporter.onBegin(fullConfig(null), suiteOf([fakeTest(0)]));
    reporter.onTestBegin(fakeTest(0), fakeResult());
    reporter.onTestEnd(fakeTest(0), fakeResult());
    reporter.onEnd(fullResult());

    expect(readdirSync(join(spool, 'run-legacy'))).toEqual(['pw-legacy-0']);
    const record = readShardsRecord(spool, 'run-legacy', 'pw-legacy-0');
    expect(record).toMatchObject({ producer_id: 'pw-legacy-0' });
    // No shard config was declared, so no slot is claimed.
    expect(record.shard_index).toBeUndefined();
  });

  it('fails at construction for a producer_id that is not spool-safe', () => {
    const spool = tempSpool();
    mkdirSync(spool, { recursive: true });
    for (const producerId of ['../escape', 'a/b', 'has space', 'x'.repeat(129), 'ünicode']) {
      expect(
        () =>
          new EvidgateReporter({
            spool_dir: spool,
            run_id: 'run-bad-id',
            producer_id: producerId,
            engineVersion: '1.63.0',
          }),
      ).toThrow(/producer_id/);
    }
    expect(readdirSync(spool)).toEqual([]);
  });
});
