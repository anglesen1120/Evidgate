// 04.3.3-T1.2 (AC3): the merge-reports replay preserves each shard's origin by not
// renumerating anything — it emits nothing at all.
//
// What Playwright 1.63 actually gives a custom reporter in a merge replay (read from the
// installed package):
//   - the merged config is `shard: null` (`mergeConfigs`, node_modules/playwright/lib/runner/index.js),
//     so deriving an id from it would silently name every shard `shard-1`;
//   - each blob's own shard metadata (`shard: {total, current}` in the `onBlobReportMetadata`
//     line) stays inside the merge code, which only uses it to sort the blobs;
//   - the per-blob `onReportConfigure({reportPath, config})` hook that carries a blob's shard
//     config reaches built-in reporters only: a custom reporter is wrapped in
//     `ReporterV2Wrapper` (lib/runner/index.js:1697-1715), which has no such method.
// There is therefore no per-event shard identity on the API this reporter can reach, so any
// emission here would have to guess a producer id and would reuse sequences the original
// shard processes already sent. The reporter refuses, and the streams stay exactly as they
// were — which is the AC's "no renumbering at merge" in its strongest form.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullConfig, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-merge-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
  vi.restoreAllMocks();
});

/** The merged config: `shard` is null and projects keep their shard identity. */
function mergedConfig(): FullConfig {
  return {
    configFile: undefined,
    rootDir: '/repo',
    shard: null,
    workers: 3,
    version: '1.63.0',
    metadata: {},
    projects: [],
  } as unknown as FullConfig;
}

function shardConfig(total: number, current: number): FullConfig {
  return { ...mergedConfig(), shard: { total, current } };
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

/** Every spool file of the run, keyed by relative path: a byte snapshot of the streams. */
function snapshot(spool: string, runId: string): Record<string, string> {
  const files: Record<string, string> = {};
  const runDir = join(spool, runId);
  for (const producer of readdirSync(runDir).sort()) {
    for (const file of readdirSync(join(runDir, producer)).sort()) {
      files[`${producer}/${file}`] = readFileSync(join(runDir, producer, file), 'utf8');
    }
  }
  return files;
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

/** One shard's own run: the production path that already ingested the events (AC1). */
function runShard(
  spool: string,
  runId: string,
  total: number,
  current: number,
  tests: TestCase[],
): void {
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: runId,
    engineVersion: '1.63.0',
    argv: ['test', `--shard=${String(current)}/${String(total)}`],
  });
  reporter.onBegin(shardConfig(total, current), suiteOf(tests));
  for (const test of tests) {
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(test, fakeResult());
  }
  reporter.onEnd(fullResult());
}

/** The merge replay: one instance, the merged config, every shard's events in turn. */
function replayMerge(
  spool: string,
  runId: string,
  argv: readonly string[],
  perShardTests: readonly TestCase[][],
): EvidgateReporter {
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: runId,
    engineVersion: '1.63.0',
    argv,
  });
  reporter.onBegin(mergedConfig(), suiteOf(perShardTests.flat()));
  for (const tests of perShardTests) {
    for (const test of tests) {
      reporter.onTestBegin(test, fakeResult());
      reporter.onTestEnd(test, fakeResult());
    }
  }
  return reporter;
}

describe('AC3: merge-reports keeps each shard’s producer and sequence', () => {
  it('emits nothing during the replay and leaves both shard streams byte-identical', () => {
    const spool = tempSpool();
    const runId = 'run-merge';
    const shard1 = [fakeTest(0), fakeTest(1)];
    const shard2 = [fakeTest(2)];
    runShard(spool, runId, 2, 1, shard1);
    runShard(spool, runId, 2, 2, shard2);

    const first = readEnvelopes(spool, runId, 'shard-1').map((envelope) => envelope.sequence);
    const second = readEnvelopes(spool, runId, 'shard-2').map((envelope) => envelope.sequence);
    const before = snapshot(spool, runId);

    const stderr = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    const reporter = replayMerge(spool, runId, ['merge-reports', 'blob-report'], [shard1, shard2]);

    // Not one envelope, and no new producer directory (no `shard-1` collision, no guess).
    expect(stderr).toHaveBeenCalledTimes(1);
    expect(String(stderr.mock.calls[0]?.[0])).toContain('merge-reports replay: not emitting');
    // AC3's status override never applies to a replay.
    expect(reporter.onEnd(fullResult())).toBeUndefined();
    expect(snapshot(spool, runId)).toEqual(before);
    expect(readdirSync(join(spool, runId)).sort()).toEqual(['shard-1', 'shard-2']);

    // Each shard keeps its own id and its own contiguous sequence: nothing was renumbered.
    expect(readEnvelopes(spool, runId, 'shard-1').map((envelope) => envelope.sequence)).toEqual(
      first,
    );
    expect(readEnvelopes(spool, runId, 'shard-2').map((envelope) => envelope.sequence)).toEqual(
      second,
    );
    // The replay also wrote nothing new anywhere: still only the two shard directories.
    expect(readdirSync(spool)).toEqual([runId]);
  });

  it('does not create a spool dir when the replay is the only process', () => {
    const spool = tempSpool();
    const stderr = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);

    const reporter = replayMerge(spool, 'run-merge-only', ['merge-reports'], [[fakeTest(0)]]);
    expect(reporter.onEnd(fullResult())).toBeUndefined();

    expect(readdirSync(spool)).toEqual([]);
    expect(stderr).toHaveBeenCalledTimes(1);
  });

  it('still emits for a normal run whose config has no shard (null is not merge mode)', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-unsharded',
      engineVersion: '1.63.0',
      argv: ['test'],
    });
    const test = fakeTest(0);
    reporter.onBegin(mergedConfig(), suiteOf([test]));
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(test, fakeResult());
    reporter.onEnd(fullResult());

    // Unsharded is `shard-1` (T54), because the argv says this is a test run, not a replay.
    expect(readdirSync(join(spool, 'run-unsharded'))).toEqual(['shard-1']);
    expect(readEnvelopes(spool, 'run-unsharded', 'shard-1').length).toBeGreaterThan(0);
  });
});
