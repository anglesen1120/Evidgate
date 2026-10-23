// 04.3.1-T1.2: callback → envelope fidelity (AC1, AC2, AC6). The fakes carry only the
// members the reporter reads; Playwright hands it real TestCase/TestResult objects.
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase, TestResult, TestStep } from '@playwright/test/reporter';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';
import { CHECKPOINT_TMP_FILE } from './spool.js';

const isWindows = process.platform === 'win32';
const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-cb-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fakeTest(overrides: Record<string, unknown> = {}): TestCase {
  return {
    title: 'adds two numbers',
    location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
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
    ...overrides,
  } as unknown as TestResult;
}

function fakeStep(overrides: Record<string, unknown> = {}): TestStep {
  return {
    title: 'click',
    category: 'pw:api',
    duration: 4,
    location: { file: 'tests/math.spec.ts', line: 5, column: 3 },
    steps: [],
    ...overrides,
  } as unknown as TestStep;
}

function fullResult(status: 'passed' | 'failed' | 'timedout' | 'interrupted'): {
  status: 'passed' | 'failed' | 'timedout' | 'interrupted';
  startTime: Date;
  duration: number;
} {
  return { status, startTime: new Date(), duration: 100 };
}

function makeReporter(overrides: Partial<EvidgateReporterOptions> = {}): {
  reporter: EvidgateReporter;
  spool: string;
} {
  const spool = tempSpool();
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: 'run-1',
    producer_id: 'pw-0',
    engineVersion: '1.63.0',
    ...overrides,
  });
  return { reporter, spool };
}

function readEnvelopes(spool: string, runId = 'run-1', producerId = 'pw-0'): EventEnvelope[] {
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

describe('callback → envelope fidelity', () => {
  it('AC1: 3 tests produce one test.started and one test.finished per attempt', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    for (let index = 0; index < 3; index += 1) {
      const test = fakeTest({ title: `test ${index}` });
      reporter.onTestBegin(test, fakeResult());
      reporter.onTestEnd(test, fakeResult());
    }
    reporter.onEnd(fullResult('passed'));

    const envelopes = readEnvelopes(spool);
    expect(envelopes.filter((item) => item.type === 'test.started')).toHaveLength(3);
    expect(envelopes.filter((item) => item.type === 'test.finished')).toHaveLength(3);
    for (const envelope of envelopes) {
      expect(envelope.schema_version).toBe(1);
      expect(envelope.event_id).toMatch(
        /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
      );
      expect(envelope.producer_id).toBe('pw-0');
      expect(typeof envelope.sequence).toBe('number');
      expect(envelope.execution_key).toMatch(/^[0-9a-f]{64}$/);
      expect(envelope.attempt_no).toBeGreaterThanOrEqual(1);
      expect(typeof envelope.type).toBe('string');
      expect(envelope.occurred_at).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/);
      expect(envelope.ext.schema).toBe('playwright/1');
    }
  });

  it('AC2: onTestBegin emits test.started with no status or duration', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult({ status: 'passed', duration: 99 }));
    reporter.onEnd(fullResult('passed'));

    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started).toBeDefined();
    expect(started).not.toHaveProperty('status');
    expect(started).not.toHaveProperty('duration_ms');
  });

  it('AC2: onTestEnd carries the raw status string and result.duration', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestEnd(fakeTest(), fakeResult({ status: 'timedOut', duration: 4321 }));
    reporter.onEnd(fullResult('failed'));

    const finished = readEnvelopes(spool).find((item) => item.type === 'test.finished');
    expect(finished?.status).toBe('timedOut');
    expect(finished?.duration_ms).toBe(4321);
  });

  it('AC2: onBegin/onEnd emit run.log markers only', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onEnd(fullResult('failed'));

    const envelopes = readEnvelopes(spool);
    expect(envelopes.map((item) => item.type)).toEqual(['run.log', 'run.log', 'run.log']);
    expect(envelopes[0]?.ext.capability).toBeDefined();
    expect(envelopes[0]?.ext.marker).toBeUndefined();
    expect(envelopes[1]?.ext.marker).toBe('run_begin');
    expect(envelopes[2]?.ext.marker).toBe('run_end');
    expect(envelopes[2]?.status).toBe('failed');
  });

  it('AC4: attempt_no is result.retry + 1', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult({ retry: 2 }));
    reporter.onTestEnd(fakeTest(), fakeResult({ retry: 2 }));
    reporter.onEnd(fullResult('passed'));

    const attempts = readEnvelopes(spool)
      .filter((item) => item.type !== 'run.log')
      .map((item) => item.attempt_no);
    expect(attempts).toEqual([3, 3]);
  });

  it('AC6: steps:true emits one test.step per step, recursively', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    // User-authored steps (category `test.step`) keep their title; `pw:api`/`expect`
    // step titles are dropped (PS1), so these carry the category explicitly.
    const nested = fakeStep({
      title: 'outer',
      category: 'test.step',
      steps: [fakeStep({ title: 'inner', category: 'test.step' })],
    });
    reporter.onTestEnd(
      fakeTest(),
      fakeResult({ steps: [fakeStep({ title: 'first', category: 'test.step' }), nested] }),
    );
    reporter.onEnd(fullResult('passed'));

    const steps = readEnvelopes(spool).filter((item) => item.type === 'test.step');
    expect(steps.map((item) => item.ext.title)).toEqual(['first', 'outer', 'inner']);
    expect(steps.map((item) => item.duration_ms)).toEqual([4, 4, 4]);
  });

  it('AC6: steps:false emits no test.step and still passes AC1', () => {
    const { reporter, spool } = makeReporter({ steps: false });
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    reporter.onTestEnd(fakeTest(), fakeResult({ steps: [fakeStep()] }));
    reporter.onEnd(fullResult('passed'));

    const envelopes = readEnvelopes(spool);
    expect(envelopes.filter((item) => item.type === 'test.step')).toHaveLength(0);
    expect(envelopes.filter((item) => item.type === 'test.started')).toHaveLength(1);
    expect(envelopes.filter((item) => item.type === 'test.finished')).toHaveLength(1);
  });

  it('AC6: supports.step_events follows the steps option', () => {
    const { reporter, spool } = makeReporter({ steps: false });
    reporter.onBegin();
    reporter.onEnd(fullResult('passed'));

    const carrier = readEnvelopes(spool)[0];
    // The capability carrier is our own document; read the flag through a narrow shape.
    const capability = carrier?.ext.capability as
      { supports?: { step_events?: boolean } } | undefined;
    expect(capability?.supports?.step_events).toBe(false);
  });

  it('surfaces a spool write failure from onEnd, never from a callback', () => {
    if (isWindows) return; // Windows is skipped, as the finding allows.
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    // Inject the failure without permissions (a root user ignores mode bits): the
    // checkpoint tmp path is a directory, so the atomic checkpoint write cannot open it.
    // This still fails after the unlink + O_EXCL hardening (unlinking a directory throws).
    mkdirSync(join(spool, 'run-1', 'pw-0', CHECKPOINT_TMP_FILE));
    expect(() => reporter.onTestEnd(fakeTest(), fakeResult())).not.toThrow();
    expect(() => reporter.onEnd(fullResult('passed'))).toThrow();
  });
});
