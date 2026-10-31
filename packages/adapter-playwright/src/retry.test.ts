// 04.3.2-T2.1/T2.2: retries through the real reporter (AC5, AC7). Each attempt is its
// own `test.finished` with `attempt_no = retry + 1` — a flaky test is attempt 1 `failed`
// + attempt 2 `passed`, never one merged status — and a `fixme`/`skip` annotation that
// appears only on a retry keeps `attempt_no` contiguous. Every emitted envelope is
// validated against contracts/schemas/event-envelope.v1.json with the same Ajv 8 setup
// capability.test.ts uses; no `ext` value may carry a stack frame or the raw error keys.
// The harness (fake TestCase/TestResult, temp spool, JSONL read-back) is callbacks.test.ts's.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';

const contractsDir = join(dirname(fileURLToPath(import.meta.url)), '../../../contracts');

const ajv = new Ajv2020({ allErrors: true, strict: false });
ajvFormats.default(ajv);

function compileContract(relativePath: string): ValidateFunction {
  const document = JSON.parse(readFileSync(join(contractsDir, relativePath), 'utf8')) as Record<
    string,
    unknown
  >;
  document['$id'] = `urn:evidgate:contract:${basename(relativePath, '.json')}`;
  return ajv.compile(document);
}

const validateEnvelope = compileContract('schemas/event-envelope.v1.json');

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-retry-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

type Annotation = { type: string; description?: string };

function fakeTest(overrides: Record<string, unknown> = {}): TestCase {
  return {
    id: 'retry-test-1',
    title: 'adds two numbers',
    location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
    annotations: [] as Annotation[],
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'chromium' }) },
    ...overrides,
  } as unknown as TestCase;
}

function fakeResult(overrides: Record<string, unknown> = {}): TestResult {
  return {
    status: 'passed',
    duration: 12,
    retry: 0,
    steps: [],
    annotations: [] as Annotation[],
    errors: [],
    ...overrides,
  } as unknown as TestResult;
}

/** A Playwright-shaped failure: ANSI in the message, the full stack on `stack`. */
function failure(): Record<string, unknown> {
  const message =
    '\u001b[31mError: expect(received).toBe(expected)\u001b[39m\n\nExpected: 2\nReceived: 3';
  const stack = `${message}\n    at /repo/tests/math.spec.ts:4:13\n    at processTicksAndRejections (node:internal/process/task_queues:95:5)`;
  const error = {
    message,
    stack,
    location: { file: '/repo/tests/math.spec.ts', line: 4, column: 13 },
  };
  return { status: 'failed', error, errors: [error] };
}

function makeReporter(): { reporter: EvidgateReporter; spool: string } {
  const spool = tempSpool();
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: 'run-1',
    producer_id: 'pw-0',
    engineVersion: '1.63.0',
  });
  return { reporter, spool };
}

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

/** Drives one test through the given attempts the way Playwright calls the reporter. */
function run(test: TestCase, attempts: readonly TestResult[]): EventEnvelope[] {
  const { reporter, spool } = makeReporter();
  reporter.onBegin();
  for (const result of attempts) {
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
  }
  void reporter.onEnd({ status: 'failed', startTime: new Date(), duration: 1 });
  return readEnvelopes(spool);
}

function ofType(envelopes: readonly EventEnvelope[], type: string): EventEnvelope[] {
  return envelopes.filter((item) => item.type === type);
}

function expectValidAndClean(envelopes: readonly EventEnvelope[]): void {
  for (const envelope of envelopes) {
    const valid = validateEnvelope(envelope);
    expect(validateEnvelope.errors ?? [], `${envelope.type} #${envelope.sequence}`).toEqual([]);
    expect(valid).toBe(true);
    const ext = JSON.stringify(envelope.ext);
    expect(ext).not.toContain('\\n    at ');
    expect(ext).not.toContain('node:internal');
    expect(ext).not.toContain('\\u001b');
    for (const key of ['errors', 'error', 'stack']) {
      expect(envelope.ext).not.toHaveProperty(key);
    }
  }
}

describe('retries through the reporter (AC5)', () => {
  it('flaky: retry 0 failed, retry 1 passed → attempt 1 failed + attempt 2 passed, never merged', () => {
    const test = fakeTest();
    const envelopes = run(test, [
      fakeResult({ retry: 0, ...failure() }),
      fakeResult({ retry: 1, status: 'passed' }),
    ]);

    const finished = ofType(envelopes, 'test.finished');
    expect(finished).toHaveLength(2);
    expect(finished.map((item) => [item.attempt_no, item.status, item.ext.normalized])).toEqual([
      [1, 'failed', 'failed'],
      [2, 'passed', 'passed'],
    ]);
    expect(finished[0]?.ext.error_summary).toBe('Error: expect(received).toBe(expected)');
    expect(finished[1]?.ext).not.toHaveProperty('error_summary');
    for (const item of finished) {
      expect(item.ext).not.toHaveProperty('xfail');
      expect(item.ext).not.toHaveProperty('skip_reason');
    }
    // Both attempts share the identity, so they line up under one execution.
    expect(finished[0]?.execution_key).toBe(finished[1]?.execution_key);
    expect(ofType(envelopes, 'test.started').map((item) => item.attempt_no)).toEqual([1, 2]);
    expectValidAndClean(envelopes);
  });

  it('retries 0, 1, 2 all failed → attempt_no 1, 2, 3 on test.started and test.finished', () => {
    const envelopes = run(fakeTest(), [
      fakeResult({ retry: 0, ...failure() }),
      fakeResult({ retry: 1, ...failure() }),
      fakeResult({ retry: 2, ...failure() }),
    ]);

    expect(ofType(envelopes, 'test.started').map((item) => item.attempt_no)).toEqual([1, 2, 3]);
    const finished = ofType(envelopes, 'test.finished');
    expect(finished.map((item) => item.attempt_no)).toEqual([1, 2, 3]);
    expect(finished.map((item) => item.status)).toEqual(['failed', 'failed', 'failed']);
    expect(finished.map((item) => item.ext.normalized)).toEqual(['failed', 'failed', 'failed']);
    // Events stay in callback order: started(n) precedes finished(n).
    const testEvents = envelopes
      .filter((item) => item.type === 'test.started' || item.type === 'test.finished')
      .map((item) => `${item.type}:${item.attempt_no}`);
    expect(testEvents).toEqual([
      'test.started:1',
      'test.finished:1',
      'test.started:2',
      'test.finished:2',
      'test.started:3',
      'test.finished:3',
    ]);
    expectValidAndClean(envelopes);
  });
});

describe('annotation appearing only on a retry (AC7)', () => {
  it('fixme added on retry 1 → attempt_no 1, 2 contiguous; only attempt 2 is skipped/fixme', () => {
    const envelopes = run(fakeTest(), [
      fakeResult({ retry: 0, ...failure() }),
      fakeResult({
        retry: 1,
        status: 'skipped',
        annotations: [{ type: 'fixme', description: 'quarantined after first failure' }],
      }),
    ]);

    expect(ofType(envelopes, 'test.started').map((item) => item.attempt_no)).toEqual([1, 2]);
    const finished = ofType(envelopes, 'test.finished');
    expect(finished.map((item) => item.attempt_no)).toEqual([1, 2]);
    expect(finished[0]?.status).toBe('failed');
    expect(finished[0]?.ext.normalized).toBe('failed');
    expect(finished[0]?.ext).not.toHaveProperty('skip_reason');
    expect(finished[1]?.status).toBe('skipped');
    expect(finished[1]?.ext.normalized).toBe('skipped');
    expect(finished[1]?.ext.skip_reason).toBe('fixme');
    expectValidAndClean(envelopes);
  });

  it('skip added on retry 1 → attempt_no 1, 2 contiguous; attempt 2 skipped without skip_reason', () => {
    const envelopes = run(fakeTest(), [
      fakeResult({ retry: 0, ...failure() }),
      fakeResult({ retry: 1, status: 'skipped', annotations: [{ type: 'skip' }] }),
    ]);

    expect(ofType(envelopes, 'test.started').map((item) => item.attempt_no)).toEqual([1, 2]);
    const finished = ofType(envelopes, 'test.finished');
    expect(finished.map((item) => item.attempt_no)).toEqual([1, 2]);
    expect(finished.map((item) => item.ext.normalized)).toEqual(['failed', 'skipped']);
    for (const item of finished) expect(item.ext).not.toHaveProperty('skip_reason');
    expectValidAndClean(envelopes);
  });

  it('a fixme in test.annotations counts too (test.annotations ++ result.annotations)', () => {
    const envelopes = run(fakeTest({ annotations: [{ type: 'fixme' }] }), [
      fakeResult({ retry: 0, status: 'skipped' }),
    ]);
    const finished = ofType(envelopes, 'test.finished');
    expect(finished.map((item) => [item.attempt_no, item.ext.skip_reason])).toEqual([[1, 'fixme']]);
    expectValidAndClean(envelopes);
  });
});
