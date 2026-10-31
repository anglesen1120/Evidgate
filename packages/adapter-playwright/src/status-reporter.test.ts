// 04.3.2-T2.2: the status mapping through the real reporter — an unknown future status
// keeps its raw value, maps to `error` and emits one `unknown_status` warning (00 §8.5:
// the server marks it incomplete); `interrupted` and `timedOut` stay distinct; an xfail
// maps to `passed` + `ext.xfail`; and `test.started` never carries the status keys,
// even when the TestResult has same-named own members. Harness as in callbacks.test.ts;
// every envelope is validated against contracts/schemas/event-envelope.v1.json.
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

const STATUS_KEYS = ['normalized', 'skip_reason', 'xfail', 'error_summary'] as const;

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-status-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fakeTest(overrides: Record<string, unknown> = {}): TestCase {
  return {
    id: 'status-test-1',
    title: 'adds two numbers',
    location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
    annotations: [],
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
    annotations: [],
    errors: [],
    ...overrides,
  } as unknown as TestResult;
}

/** One test, one attempt, through onBegin → onTestBegin → onTestEnd → onEnd. */
function runOne(test: TestCase, result: TestResult): EventEnvelope[] {
  const spool = tempSpool();
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: 'run-1',
    producer_id: 'pw-0',
    engineVersion: '1.63.0',
  });
  reporter.onBegin();
  reporter.onTestBegin(test, result);
  reporter.onTestEnd(test, result);
  void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

  const dir = join(spool, 'run-1', 'pw-0');
  const envelopes: EventEnvelope[] = [];
  for (const file of readdirSync(dir)
    .filter((entry) => entry.endsWith('.jsonl'))
    .sort()) {
    for (const line of readFileSync(join(dir, file), 'utf8').split('\n')) {
      if (line.length > 0) envelopes.push(JSON.parse(line) as EventEnvelope);
    }
  }
  for (const envelope of envelopes) {
    validateEnvelope(envelope);
    expect(validateEnvelope.errors ?? [], `${envelope.type} #${envelope.sequence}`).toEqual([]);
  }
  return envelopes;
}

function single(envelopes: readonly EventEnvelope[], type: string): EventEnvelope {
  const matches = envelopes.filter((item) => item.type === type);
  expect(matches).toHaveLength(1);
  return matches[0] as EventEnvelope;
}

function warningsOf(
  envelopes: readonly EventEnvelope[],
  code: string,
): Array<{ code?: string; message?: string }> {
  return envelopes
    .filter((item) => item.type === 'run.log')
    .map((item) => item.ext.warn as { code?: string; message?: string } | undefined)
    .filter((warn): warn is { code?: string; message?: string } => warn?.code === code);
}

describe('unknown future status (04.3.2-T2.2)', () => {
  it('keeps the raw status, maps to error and emits one unknown_status warning', () => {
    const envelopes = runOne(fakeTest(), fakeResult({ status: 'flaky-future' }));

    const finished = single(envelopes, 'test.finished');
    expect(finished.status).toBe('flaky-future');
    expect(finished.ext.normalized).toBe('error');
    expect(finished.ext).not.toHaveProperty('skip_reason');
    expect(finished.ext).not.toHaveProperty('xfail');

    const warnings = warningsOf(envelopes, 'unknown_status');
    expect(warnings).toHaveLength(1);
    expect(warnings[0]?.message).toContain('flaky-future');
    expect(warnings[0]?.message).toContain('tests/math.spec.ts:3:1');
  });

  it('a known status emits no unknown_status warning', () => {
    for (const status of ['passed', 'failed', 'timedOut', 'interrupted', 'skipped']) {
      expect(warningsOf(runOne(fakeTest(), fakeResult({ status })), 'unknown_status')).toEqual([]);
    }
  });

  it('cuts an over-long unknown status to 64 chars in status and in the warning', () => {
    const long = 'z'.repeat(100);
    const envelopes = runOne(fakeTest(), fakeResult({ status: long }));

    expect(single(envelopes, 'test.finished').status).toBe('z'.repeat(64));
    const [warning] = warningsOf(envelopes, 'unknown_status');
    expect(warning?.message).toContain('z'.repeat(64));
    expect(warning?.message).not.toContain('z'.repeat(65));
  });

  it('a prototype key is an unknown status, not a mapped one', () => {
    const envelopes = runOne(fakeTest(), fakeResult({ status: 'constructor' }));
    expect(single(envelopes, 'test.finished').ext.normalized).toBe('error');
    expect(warningsOf(envelopes, 'unknown_status')).toHaveLength(1);
  });
});

describe('interrupted vs timedOut', () => {
  it('produce distinct raw and normalized values', () => {
    const interrupted = single(
      runOne(fakeTest(), fakeResult({ status: 'interrupted' })),
      'test.finished',
    );
    const timedOut = single(
      runOne(fakeTest(), fakeResult({ status: 'timedOut' })),
      'test.finished',
    );

    expect(interrupted.status).toBe('interrupted');
    expect(interrupted.ext.normalized).toBe('error');
    expect(timedOut.status).toBe('timedOut');
    expect(timedOut.ext.normalized).toBe('timed_out');
  });
});

describe('xfail through the reporter (AC6)', () => {
  it('expectedStatus failed × failed → ext.normalized passed + ext.xfail true, raw failed', () => {
    const finished = single(
      runOne(
        fakeTest({ expectedStatus: 'failed' }),
        fakeResult({
          status: 'failed',
          errors: [{ message: 'known bug #12\n    at /repo/tests/math.spec.ts:4:13' }],
        }),
      ),
      'test.finished',
    );
    expect(finished.status).toBe('failed');
    expect(finished.ext.normalized).toBe('passed');
    expect(finished.ext.xfail).toBe(true);
    expect(finished.ext.error_summary).toBe('known bug #12');
  });

  it('expectedStatus failed × passed (unexpected pass) → ext.normalized failed + ext.xfail true', () => {
    const finished = single(
      runOne(fakeTest({ expectedStatus: 'failed' }), fakeResult({ status: 'passed' })),
      'test.finished',
    );
    expect(finished.status).toBe('passed');
    expect(finished.ext.normalized).toBe('failed');
    expect(finished.ext.xfail).toBe(true);
  });
});

describe('status keys only on test.finished', () => {
  it('test.started carries none of normalized/skip_reason/xfail/error_summary', () => {
    const envelopes = runOne(
      fakeTest({ annotations: [{ type: 'fixme' }], expectedStatus: 'failed' }),
      fakeResult({ status: 'skipped', errors: [{ message: 'boom' }] }),
    );
    const started = single(envelopes, 'test.started');
    for (const key of STATUS_KEYS) expect(started.ext).not.toHaveProperty(key);
    expect(single(envelopes, 'test.finished').ext.skip_reason).toBe('fixme');
  });

  it('a same-named own member of the TestResult never leaks into ext', () => {
    const envelopes = runOne(
      fakeTest(),
      fakeResult({
        status: 'passed',
        normalized: 'forged',
        skip_reason: 'forged',
        xfail: true,
        error_summary: 'forged',
      }),
    );
    const started = single(envelopes, 'test.started');
    for (const key of STATUS_KEYS) expect(started.ext).not.toHaveProperty(key);

    const finished = single(envelopes, 'test.finished');
    expect(finished.ext.normalized).toBe('passed');
    expect(finished.ext).not.toHaveProperty('skip_reason');
    expect(finished.ext).not.toHaveProperty('xfail');
    expect(finished.ext).not.toHaveProperty('error_summary');
  });
});
