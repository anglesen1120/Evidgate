// 04.3.1-T1.2: unknown `result`/`test` fields are never forwarded — the envelope and
// its `ext` carry exactly the declared keys (04 §6 allow-list, test plan row "Unknown
// result fields"). A secret-looking key is dropped and counted.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-unknown-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function readEnvelopes(spool: string): EventEnvelope[] {
  const dir = join(spool, 'run-1', 'pw-0');
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

describe('unknown fields', () => {
  it('never forwards unknown result fields (envelope key snapshot)', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    const test = {
      title: 'adds two numbers',
      location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
      parent: { project: () => ({ name: 'chromium' }) },
      id: 'abc',
      tags: ['@smoke'],
      expectedStatus: 'passed',
      annotations: [{ type: 'skip' }],
    } as unknown as TestCase;
    const result = {
      status: 'passed',
      duration: 12,
      retry: 0,
      steps: [],
      stdout: ['noise'],
      stderr: ['noise'],
      errors: [{ message: 'boom' }],
      attachments: [{ name: 'trace', contentType: 'application/zip' }],
      workerIndex: 0,
      parallelIndex: 0,
      startTime: new Date(),
      annotations: [],
      accessToken: 'leak',
    } as unknown as TestResult;

    reporter.onBegin();
    reporter.onTestEnd(test, result);
    reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    const finished = readEnvelopes(spool).find((item) => item.type === 'test.finished');
    expect(Object.keys(finished ?? {}).sort()).toEqual([
      'attempt_no',
      'duration_ms',
      'event_id',
      'execution_key',
      'ext',
      'occurred_at',
      'producer_id',
      'schema_version',
      'sequence',
      'status',
      'type',
    ]);
    expect(Object.keys(finished?.ext ?? {}).sort()).toEqual([
      'engine',
      // 04.3.2-T2.1: `errors: [{ message: 'boom' }]` yields `error_summary`; every
      // test.finished carries `normalized`.
      'error_summary',
      'file',
      'location',
      'normalized',
      'project',
      'schema',
      'target_env',
      'title',
      'variant_hash',
    ]);
    expect(reporter.secretKeysDropped).toBe(1);
  });
});
