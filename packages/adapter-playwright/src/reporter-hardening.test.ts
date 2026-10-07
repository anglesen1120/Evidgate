// 04.3.1-T1 review hardening: the fixes from the T2 code/perf/security review that live
// in reporter.ts. Each describe names the finding it pins:
//   CR1 — printsToStdio() keeps Playwright's console reporter
//   CR2 — one durable flush per callback batch (onBegin, onTestEnd)
//   PS1 — step titles are forwarded only for user-authored step categories
//   PS2 — duration_ms is omitted when not a non-negative finite number, rounded otherwise
//   PS6 — the step tree is walked lazily, depth-first
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { TestCase, TestResult, TestStep } from '@playwright/test/reporter';
import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';

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
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-hardening-'));
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

/** The raw spool bytes, for asserting a value never reached disk at all. */
function readSpoolBytes(spool: string, runId = 'run-1', producerId = 'pw-0'): string {
  const dir = join(spool, runId, producerId);
  return readdirSync(dir)
    .filter((entry) => entry.endsWith('.jsonl'))
    .sort()
    .map((entry) => readFileSync(join(dir, entry), 'utf8'))
    .join('');
}

describe('CR1: printsToStdio', () => {
  it('returns false so Playwright adds its default console reporter', () => {
    const { reporter } = makeReporter();
    expect(reporter.printsToStdio()).toBe(false);
  });
});

describe('CR2: one durable flush per callback batch', () => {
  it('onBegin flushes the capability carrier and run_begin before any test', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();

    const lines = readSpoolBytes(spool)
      .split('\n')
      .filter((line) => line.length > 0);
    expect(lines).toHaveLength(2);
    const envelopes = lines.map((line) => JSON.parse(line) as EventEnvelope);
    expect(envelopes[0]?.ext.capability).toBeDefined();
    expect(envelopes[1]?.ext.marker).toBe('run_begin');
  });

  it('onTestEnd flushes test.finished and its steps before onEnd', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    reporter.onTestEnd(fakeTest(), fakeResult({ steps: [fakeStep()] }));

    const envelopes = readEnvelopes(spool);
    expect(envelopes.filter((item) => item.type === 'test.finished')).toHaveLength(1);
    expect(envelopes.filter((item) => item.type === 'test.step')).toHaveLength(1);
    // onEnd has not run yet, so the run_end marker must not be on disk.
    expect(envelopes.some((item) => item.ext.marker === 'run_end')).toBe(false);
  });
});

describe('PS1: step titles are forwarded only for user-authored categories', () => {
  it('never writes a pw:api/expect step title into the spool', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestEnd(
      fakeTest(),
      fakeResult({
        steps: [
          fakeStep({ title: 'Fill "hunter2"', category: 'pw:api' }),
          fakeStep({
            title: 'page.goto("https://x.test/cb?access_token=x")',
            category: 'pw:api',
          }),
          fakeStep({ title: 'expect to be visible', category: 'expect' }),
          fakeStep({ title: 'user-authored step', category: 'test.step' }),
          fakeStep({ title: 'unknown category step', category: 'weird' }),
        ],
      }),
    );

    const bytes = readSpoolBytes(spool);
    expect(bytes).not.toContain('hunter2');
    expect(bytes).not.toContain('access_token');

    const steps = readEnvelopes(spool).filter((item) => item.type === 'test.step');
    expect(steps.map((item) => item.ext.title)).toEqual([
      undefined,
      undefined,
      undefined,
      'user-authored step',
      undefined,
    ]);
    // The category and location still identify the step.
    expect(steps.map((item) => item.ext.category)).toEqual([
      'pw:api',
      'pw:api',
      'expect',
      'test.step',
      'weird',
    ]);
    expect(steps[0]?.ext.location).toEqual({ file: 'tests/math.spec.ts', line: 5, column: 3 });
  });
});

describe('PS2: duration_ms is omitted or rounded to satisfy the schema', () => {
  it('omits duration_ms for a -1 value and rounds 1.5 to 2, all schema-valid', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestEnd(
      fakeTest(),
      fakeResult({
        duration: -1,
        steps: [
          fakeStep({ title: 'unfinished', category: 'test.step', duration: -1 }),
          fakeStep({ title: 'fractional', category: 'test.step', duration: 1.5 }),
        ],
      }),
    );
    reporter.onEnd(fullResult('passed'));

    const envelopes = readEnvelopes(spool);
    for (const envelope of envelopes) {
      const valid = validateEnvelope(envelope);
      expect(valid, JSON.stringify(validateEnvelope.errors)).toBe(true);
    }

    const finished = envelopes.find((item) => item.type === 'test.finished');
    expect(finished).not.toHaveProperty('duration_ms');
    const steps = envelopes.filter((item) => item.type === 'test.step');
    expect(steps[0]).not.toHaveProperty('duration_ms');
    expect(steps[1]?.duration_ms).toBe(2);
  });
});

describe('PS6: the step tree is walked lazily in depth-first order', () => {
  it('emits every nested step, parent before child, across a flush boundary', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();

    const leaf = (index: number): TestStep =>
      fakeStep({ title: `leaf-${index}`, category: 'test.step' });
    const branch = (index: number): TestStep =>
      fakeStep({
        title: `branch-${index}`,
        category: 'test.step',
        steps: [leaf(index * 2), leaf(index * 2 + 1)],
      });
    // 33 branches + 66 leaves = 99 steps, so the walk crosses the 64-envelope threshold.
    const steps = Array.from({ length: 33 }, (_, index) => branch(index));
    reporter.onTestEnd(fakeTest(), fakeResult({ steps }));
    reporter.onEnd(fullResult('passed'));

    const titles = readEnvelopes(spool)
      .filter((item) => item.type === 'test.step')
      .map((item) => item.ext.title);
    expect(titles).toHaveLength(99);
    expect(titles[0]).toBe('branch-0');
    expect(titles[1]).toBe('leaf-0');
    expect(titles[2]).toBe('leaf-1');
    expect(titles[3]).toBe('branch-1');
    expect(titles[98]).toBe('leaf-65');
  });
});
