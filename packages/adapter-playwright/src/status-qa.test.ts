// Independent QA for 04.3.2-T2: real Playwright attempts plus consumer-visible boundaries.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { Ajv2020 } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { filterExt } from './extFilter.js';
import { MAX_WARNING_ENVELOPES, EvidgateReporter } from './reporter.js';
import { ERROR_SUMMARY_MAX, errorSummary, mapStatus } from './status.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const packageDir = join(root, 'packages/adapter-playwright');
const cli = join(root, 'node_modules/@playwright/test/cli.js');
const api = pathToFileURL(join(root, 'node_modules/@playwright/test/index.mjs')).href;
const ajv = new Ajv2020({ allErrors: true, strict: false });
ajvFormats.default(ajv);
const schema = JSON.parse(
  readFileSync(join(root, 'contracts/schemas/event-envelope.v1.json'), 'utf8'),
) as Record<string, unknown>;
schema['$id'] = 'urn:evidgate:status-qa:event-envelope';
const validate = ajv.compile(schema);
const tempDirs: string[] = [];
const realDir = mkdtempSync(join(tmpdir(), 'evidgate-status-real-qa-'));
afterAll(() => rmSync(realDir, { recursive: true, force: true }));
afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

interface RawAttempt {
  title: string;
  retry: number;
  status: string;
  expectedStatus: string;
  annotations: Array<{ type: string }>;
}

function lines<T>(file: string): T[] {
  return readFileSync(file, 'utf8')
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line) as T);
}

function spoolEvents(dir: string, runId: string): EventEnvelope[] {
  const spool = join(dir, runId, 'pw-status-qa');
  const events = readdirSync(spool)
    .filter((file) => file.endsWith('.jsonl'))
    .sort()
    .flatMap((file) => lines<EventEnvelope>(join(spool, file)));
  for (const event of events) expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
  return events;
}

function realRun(
  name: string,
  files: Record<string, string>,
  overrides: Record<string, unknown> = {},
): { events: EventEnvelope[]; raw: RawAttempt[] } {
  const dir = join(realDir, name);
  mkdirSync(dir);
  const tests = join(dir, 'tests');
  mkdirSync(tests);
  for (const [file, text] of Object.entries(files)) writeFileSync(join(tests, file), text);
  const rawFile = join(dir, 'raw.jsonl');
  const rawReporter = join(dir, 'raw-reporter.cjs');
  writeFileSync(
    rawReporter,
    `const fs = require('node:fs'); module.exports = class {
    onTestEnd(test, result) { fs.appendFileSync(${JSON.stringify(rawFile)}, JSON.stringify({title: test.title, retry: result.retry, status: result.status, expectedStatus: test.expectedStatus, annotations: result.annotations}) + '\\n'); }
  };`,
  );
  const config = join(dir, 'playwright.config.mjs');
  writeFileSync(
    config,
    `export default ${JSON.stringify({
      testDir: tests,
      outputDir: join(dir, 'output'),
      timeout: 3000,
      workers: 1,
      retries: 2,
      projects: [{ name: 'qa-local' }],
      reporter: [
        [
          join(packageDir, 'dist/index.js'),
          { spool_dir: dir, run_id: name, producer_id: 'pw-status-qa' },
        ],
        [rawReporter],
      ],
      ...overrides,
    })};`,
  );
  const env: NodeJS.ProcessEnv = { ...process.env, EVIDGATE_TARGET_ENV: 'qa-local' };
  delete env.EVIDGATE_ALLOW_UNMAPPED;
  const run = spawnSync(process.execPath, [cli, 'test', '--config', config], {
    cwd: dir,
    env,
    encoding: 'utf8',
    timeout: 25_000,
  });
  expect(run.error, `${run.stdout}\n${run.stderr}`).toBeUndefined();
  expect(run.status, `${run.stdout}\n${run.stderr}`).toBe(1);
  const events = spoolEvents(dir, name);
  const raw = lines<RawAttempt>(rawFile);
  console.log(
    `QA status real CLI ${name}: exit ${String(run.status)}, ${raw.length} attempts, ${events.length} schema-valid envelopes`,
  );
  return { events, raw };
}

const matrixSource = `import { test, expect } from ${JSON.stringify(api)};
const annotation = i => ({ annotation: { type: 'evidgate.id', description: '11111111-1111-1111-1111-' + String(i).padStart(12, '0') } });
test('flaky', annotation(1), async ({}, info) => { expect(info.retry).toBeGreaterThan(0); });
test('always failing', annotation(2), async () => { throw new Error('always failed'); });
test('expected failure', annotation(3), async () => { test.fail(); throw new Error('known bug'); });
test('unexpected pass', annotation(4), async () => { test.fail(); });
test('skip', annotation(5), async () => { test.skip(); });
test('fixme', annotation(6), async () => { test.fixme(); });
test('fixme on retry', annotation(7), async ({}, info) => { if (info.retry > 0) test.fixme(); throw new Error('first attempt failed'); });
test('timeout', annotation(8), async () => { test.setTimeout(100); await new Promise(() => {}); });
test('multiline ANSI', annotation(9), async () => { throw new Error('\\u001b[31mqa-colour first\\u001b[0m\\nsecond line\\n    at fake-stack:1:1'); });
`;

let matrix: ReturnType<typeof realRun>;
let interrupted: ReturnType<typeof realRun>;
beforeAll(() => {
  // CR-1: the real runs load dist/index.js, so compile the code under test first
  // (reporter-cli-qa.test.ts precedent); a stale or missing dist would test other code.
  execFileSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '-b'], {
    cwd: packageDir,
    stdio: 'pipe',
  });
  matrix = realRun('matrix', { 'matrix.spec.mjs': matrixSource });
  // A filesystem barrier ensures the second worker is active before maxFailures stops it.
  const started = join(realDir, 'worker-started');
  interrupted = realRun(
    'interrupted',
    {
      'waiting.spec.mjs': `import { test } from ${JSON.stringify(api)}; import { writeFileSync } from 'node:fs';
      test('interrupted worker', { annotation: { type: 'evidgate.id', description: '22222222-2222-2222-2222-222222222222' } }, async () => { writeFileSync(${JSON.stringify(started)}, 'ready'); await new Promise(() => {}); });`,
      'failure.spec.mjs': `import { test } from ${JSON.stringify(api)}; import { existsSync } from 'node:fs';
      test('stop run', { annotation: { type: 'evidgate.id', description: '33333333-3333-3333-3333-333333333333' } }, async () => { while (!existsSync(${JSON.stringify(started)})) await new Promise(resolve => setTimeout(resolve, 10)); throw new Error('stop after one failure'); });`,
    },
    { workers: 2, retries: 0, maxFailures: 1, timeout: 15_000 },
  );
}, 120_000);

function finished(title: string): EventEnvelope[] {
  return matrix.events.filter(
    (event) => event.type === 'test.finished' && event.ext.title === title,
  );
}

function assertMapping(
  title: string,
  raw: string[],
  normalized: string[],
  extra: Record<string, unknown> = {},
): void {
  const events = finished(title);
  expect(events.map((event) => event.status)).toEqual(raw);
  expect(events.map((event) => event.ext.normalized)).toEqual(normalized);
  for (const event of events) {
    for (const key of ['skip_reason', 'xfail']) {
      if (key in extra) expect(event.ext[key]).toEqual(extra[key]);
      else expect(event.ext).not.toHaveProperty(key);
    }
  }
}

describe('real Playwright status acceptance', () => {
  it('AC5 preserves the failed and passing flaky attempts separately', () => {
    assertMapping('flaky', ['failed', 'passed'], ['failed', 'passed']);
  });
  it('AC5 preserves all three always-failing attempts', () => {
    assertMapping('always failing', ['failed', 'failed', 'failed'], ['failed', 'failed', 'failed']);
  });
  it('AC6 maps expected failure to normalized passed with xfail', () => {
    assertMapping('expected failure', ['failed'], ['passed'], { xfail: true });
  });
  it('unexpected passes fail closed on every retry', () => {
    assertMapping(
      'unexpected pass',
      ['passed', 'passed', 'passed'],
      ['failed', 'failed', 'failed'],
      { xfail: true },
    );
  });
  it('AC6 distinguishes skip from fixme', () => {
    assertMapping('skip', ['skipped'], ['skipped']);
    assertMapping('fixme', ['skipped'], ['skipped'], { skip_reason: 'fixme' });
  });
  it('AC7 fixme added only on a retry preserves contiguous attempts', () => {
    const attempts = finished('fixme on retry');
    expect(attempts.map((event) => event.attempt_no)).toEqual([1, 2]);
    expect(attempts.map((event) => event.status)).toEqual(['failed', 'skipped']);
    expect(attempts.map((event) => event.ext.normalized)).toEqual(['failed', 'skipped']);
    expect(attempts[0]?.ext.skip_reason).toBeUndefined();
    expect(attempts[1]?.ext.skip_reason).toBe('fixme');
    expect(
      matrix.raw
        .find((attempt) => attempt.title === 'fixme on retry' && attempt.retry === 0)
        ?.annotations.some((annotation) => annotation.type === 'fixme'),
    ).toBe(false);
    expect(
      matrix.raw
        .find((attempt) => attempt.title === 'fixme on retry' && attempt.retry === 1)
        ?.annotations.some((annotation) => annotation.type === 'fixme'),
    ).toBe(true);
  });
  it('AC6 maps timedOut to timed_out on all retries', () => {
    assertMapping(
      'timeout',
      ['timedOut', 'timedOut', 'timedOut'],
      ['timed_out', 'timed_out', 'timed_out'],
    );
  });
  it('AC6 maps a real interrupted worker to error, distinct from timedOut', () => {
    const event = interrupted.events.find(
      (entry) => entry.type === 'test.finished' && entry.ext.title === 'interrupted worker',
    );
    expect(event?.status).toBe('interrupted');
    expect(event?.ext.normalized).toBe('error');
    expect(interrupted.raw.find((entry) => entry.title === 'interrupted worker')?.status).toBe(
      'interrupted',
    );
  });
  it('all real attempts preserve the raw status and contiguous matching start/end pairs', () => {
    for (const run of [matrix, interrupted]) {
      for (const title of new Set(run.raw.map((attempt) => attempt.title))) {
        const actual = run.raw.filter((attempt) => attempt.title === title);
        const ends = run.events.filter(
          (event) => event.type === 'test.finished' && event.ext.title === title,
        );
        const begins = run.events.filter(
          (event) => event.type === 'test.started' && event.ext.title === title,
        );
        expect(ends.map((event) => event.status)).toEqual(actual.map((attempt) => attempt.status));
        expect(ends.map((event) => event.attempt_no)).toEqual(actual.map((_, index) => index + 1));
        expect(begins.map((event) => event.attempt_no)).toEqual(
          ends.map((event) => event.attempt_no),
        );
        expect(ends.map((event) => event.attempt_no)).toEqual(
          actual.map((attempt) => attempt.retry + 1),
        );
        for (const event of [...begins, ...ends]) {
          expect(event.ext.case_id).toMatch(/^[a-f0-9-]{36}$/);
          expect(event.ext.variant_hash).toBe(ends[0]?.ext.variant_hash);
          expect(event.ext.target_env).toBe('qa-local');
        }
      }
    }
  });
  it('real errors expose only a bounded first line, never raw errors, ANSI, or stack frames in ext', () => {
    for (const event of finished('multiline ANSI')) {
      expect(event.ext.error_summary).toContain('qa-colour first');
      expect(event.ext.error_summary).not.toContain('second line');
    }
    expect(finished('expected failure')[0]?.ext.error_summary).toContain('known bug');
    for (const event of [...matrix.events, ...interrupted.events]) {
      const serialized = JSON.stringify(event.ext);
      expect(serialized).not.toContain('    at ');
      expect(serialized).not.toContain('\\u001b');
      for (const key of ['error', 'errors', 'stack', 'attempt'])
        expect(event.ext).not.toHaveProperty(key);
      if (typeof event.ext.error_summary === 'string') {
        expect(event.ext.error_summary.length).toBeLessThanOrEqual(ERROR_SUMMARY_MAX);
        expect(event.ext.error_summary).not.toMatch(/[\r\n]/);
      }
      if (event.type !== 'test.finished')
        for (const key of ['normalized', 'skip_reason', 'xfail', 'error_summary'])
          expect(event.ext).not.toHaveProperty(key);
    }
  });
});

function synthetic(
  results: Array<Record<string, unknown>>,
  annotations: Array<{ type: string }> = [],
): EventEnvelope[] {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-status-synthetic-qa-'));
  tempDirs.push(dir);
  const reporter = new EvidgateReporter({
    spool_dir: dir,
    run_id: 'synthetic',
    producer_id: 'pw-status-qa',
    engineVersion: '1.63.0',
    identity: () => ({
      execution_key: '0'.repeat(64),
      case_id: '44444444-4444-4444-4444-444444444444',
      variant_hash: `sha256:${'0'.repeat(64)}`,
      target_env: 'qa-local',
    }),
  });
  reporter.onBegin();
  const test = {
    id: 'synthetic',
    title: 'synthetic',
    location: { file: 'synthetic.spec.ts', line: 1, column: 1 },
    annotations,
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'qa-local' }) },
  } as unknown as TestCase;
  results.forEach((overrides, retry) => {
    // Deliberately no result.annotations: the older public result shape remains supported.
    const result = {
      status: 'passed',
      retry,
      duration: 1,
      steps: [],
      errors: [],
      ...overrides,
    } as unknown as TestResult;
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
  });
  void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });
  return spoolEvents(dir, 'synthetic');
}

describe('negative and boundary status QA', () => {
  it('accepts older results without annotations and lets fixme win over skip', () => {
    const events = synthetic([{ status: 'skipped' }], [{ type: 'skip' }, { type: 'fixme' }]);
    expect(events.find((event) => event.type === 'test.finished')?.ext.skip_reason).toBe('fixme');
  });
  it('uses value-only errors, removes OSC hyperlinks and truncates at a surrogate-safe boundary', () => {
    const events = synthetic([
      {
        status: 'failed',
        errors: [
          {
            value: `\u001b]8;;https://example.invalid\u0007${'x'.repeat(255)}😀suffix\u001b]8;;\u0007\n    at synthetic:1:1`,
            stack: 'must not leak',
          },
        ],
      },
    ]);
    const ext = events.find((event) => event.type === 'test.finished')?.ext;
    expect(ext?.error_summary).toBe('x'.repeat(255));
    expect(ext).not.toHaveProperty('errors');
    expect(ext).not.toHaveProperty('stack');
  });
  it('caps a very long single-line error at 256 units', () => {
    expect(errorSummary([{ message: 'a'.repeat(100_000) }])).toBe('a'.repeat(256));
  });
  it('skips empty first lines and ignores stack-only error members', () => {
    expect(
      errorSummary([{ message: '\nnever later', value: 'not used' }, { message: 'usable\rnext' }]),
    ).toBe('usable');
    const events = synthetic([{ errors: [{ stack: '    at only-stack:1:1' }] }]);
    expect(events.find((event) => event.type === 'test.finished')?.ext).not.toHaveProperty(
      'error_summary',
    );
  });
  it('unknown statuses all finish as error and warn once per distinct raw status (CR-2)', () => {
    const statuses = [
      ...Array.from({ length: MAX_WARNING_ENVELOPES + 3 }, () => 'future-status'),
      'other-future',
    ];
    const events = synthetic(statuses.map((status) => ({ status })));
    const ends = events.filter((event) => event.type === 'test.finished');
    expect(ends.map((event) => event.status)).toEqual(statuses);
    for (const event of ends) expect(event.ext.normalized).toBe('error');
    const warnings = events
      .filter((event) => event.type === 'run.log' && event.ext.warn !== undefined)
      .map((event) => event.ext.warn as { code: string; message: string });
    const unknown = warnings.filter((warning) => warning.code === 'unknown_status');
    expect(unknown).toHaveLength(2);
    expect(unknown[0]?.message).toContain("'future-status'");
    expect(unknown[1]?.message).toContain("'other-future'");
    expect(warnings.some((warning) => warning.code === 'warnings_truncated')).toBe(false);
    expect(ends.map((event) => event.attempt_no)).toEqual(
      Array.from({ length: statuses.length }, (_, index) => index + 1),
    );
  });
  it.each(['', 'PASSED', 'constructor', '__proto__', 'toString'])(
    'unknown raw %j is error without accepting inherited table members',
    (status) => {
      const events = synthetic([{ status }]);
      expect(events.find((event) => event.type === 'test.finished')?.status).toBe(status);
      expect(events.find((event) => event.type === 'test.finished')?.ext.normalized).toBe('error');
      expect(
        events.filter(
          (event) => (event.ext.warn as { code?: string } | undefined)?.code === 'unknown_status',
        ),
      ).toHaveLength(1);
    },
  );
  it('raw future status is capped without splitting a supplementary character', () => {
    expect(mapStatus({ status: `${'x'.repeat(63)}😀suffix`, annotations: [] }).raw).toBe(
      'x'.repeat(63),
    );
  });
  it('the allow-list discards raw errors and stack members', () => {
    expect(
      filterExt({
        schema: 'playwright/1',
        normalized: 'failed',
        error_summary: 'brief',
        errors: [{ message: 'raw' }],
        error: { stack: 'raw' },
        stack: 'raw',
      }).ext,
    ).toEqual({ schema: 'playwright/1', normalized: 'failed', error_summary: 'brief' });
  });
});
