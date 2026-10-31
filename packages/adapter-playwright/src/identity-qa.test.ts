// Independent QA for 04.3.2-T1: consumer-visible metadata and annotation boundaries.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { createIdentityResolver, type WarningPayload } from './identity.js';
import { EvidgateReporter } from './reporter.js';

const ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const OTHER = '11111111-2222-3333-4444-555555555555';
const dirs: string[] = [];
const keys = ['EVIDGATE_TARGET_ENV', 'EVIDGATE_ALLOW_UNMAPPED'] as const;
const saved: Record<string, string | undefined> = {};
const fullResult = { status: 'passed' as const, startTime: new Date(0), duration: 1 };

function testCase(id: string, params: unknown = {}, descriptions: string[] = [ID]): TestCase {
  const project = { name: 'chromium', metadata: { evidgate: { params } } };
  return {
    id,
    title: `parameter ${id}`,
    location: { file: '/tmp/customer/a.spec.ts', line: 10, column: 1 },
    annotations: descriptions.map((description) => ({ type: 'evidgate.id', description })),
    parent: { project: () => project },
  } as unknown as TestCase;
}

function sidecarTest(id: string, title: string, file = '/tmp/customer/a.spec.ts'): TestCase {
  return {
    id,
    title,
    location: { file, line: 10, column: 1 },
    annotations: [],
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as TestCase;
}

function envelopes(spool: string): EventEnvelope[] {
  const dir = join(spool, 'qa-run', 'qa-producer');
  return readdirSync(dir)
    .filter((name) => name.endsWith('.jsonl'))
    .sort()
    .flatMap((name) =>
      readFileSync(join(dir, name), 'utf8')
        .split('\n')
        .filter(Boolean)
        .map((line) => JSON.parse(line) as EventEnvelope),
    );
}

beforeEach(() => {
  for (const key of keys) {
    saved[key] = process.env[key];
    delete process.env[key];
  }
});

afterEach(() => {
  for (const key of keys) {
    if (saved[key] === undefined) delete process.env[key];
    else process.env[key] = saved[key];
  }
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe('independent identity QA', () => {
  it('detects duplicate IDs in parameterized tests sharing one source location', () => {
    const warnings: WarningPayload[] = [];
    const resolve = createIdentityResolver({ warn: (warning) => warnings.push(warning) });
    resolve(testCase('parameter-a'));
    resolve(testCase('parameter-b'));
    expect(warnings.map((warning) => warning.code)).toContain('duplicate_case_id');
    expect(warnings[0]?.message).toContain('/tmp/customer/a.spec.ts:10:1');
  });

  it('does not warn for the same test under two Playwright projects', () => {
    const warnings: WarningPayload[] = [];
    const resolve = createIdentityResolver({ warn: (warning) => warnings.push(warning) });
    const base = {
      title: 'adds two numbers',
      location: { file: '/tmp/customer/a.spec.ts', line: 10, column: 1 },
      annotations: [{ type: 'evidgate.id', description: ID }],
    };
    const chromium = { name: 'chromium', metadata: { evidgate: { params: {} } } };
    const firefox = { name: 'firefox', metadata: { evidgate: { params: {} } } };
    resolve({
      ...base,
      id: 'chromium-a',
      titlePath: () => ['', 'chromium', 'a.spec.ts', 'adds two numbers'],
      parent: { project: () => chromium },
    } as unknown as TestCase);
    resolve({
      ...base,
      id: 'firefox-a',
      titlePath: () => ['', 'firefox', 'a.spec.ts', 'adds two numbers'],
      parent: { project: () => firefox },
    } as unknown as TestCase);
    expect(warnings.map((warning) => warning.code)).not.toContain('duplicate_case_id');
  });

  it('does not warn for two tests mapped by one sidecar file', () => {
    const warnings: WarningPayload[] = [];
    const resolve = createIdentityResolver({
      sidecar: { lookup: () => ID, warnings: [], droppedWarnings: 0 },
      warn: (warning) => warnings.push(warning),
    });
    resolve(sidecarTest('t1', 'first test'));
    resolve(sidecarTest('t2', 'second test'));
    expect(warnings.map((warning) => warning.code)).not.toContain('duplicate_case_id');
  });

  it('still warns when two different sidecar files claim one case id', () => {
    const warnings: WarningPayload[] = [];
    const resolve = createIdentityResolver({
      sidecar: { lookup: () => ID, warnings: [], droppedWarnings: 0 },
      warn: (warning) => warnings.push(warning),
    });
    resolve(sidecarTest('t1', 'first test', '/tmp/customer/a.spec.ts'));
    resolve(sidecarTest('t2', 'second test', '/tmp/customer/b.spec.ts'));
    expect(warnings.map((warning) => warning.code)).toContain('duplicate_case_id');
  });

  it.each([
    ['upper-case UUID', [ID.toUpperCase()], ID, false],
    ['two equal UUIDs', [ID, ID.toUpperCase()], ID, false],
    ['two different UUIDs', [ID, OTHER], undefined, true],
    ['whitespace around UUID', [` ${ID} `], undefined, false],
    ['not a UUID', ['invalid-identifier'], undefined, false],
    ['nil UUID', ['00000000-0000-0000-0000-000000000000'], undefined, false],
  ] as const)(
    'handles %s annotations without silently selecting a conflict',
    (_name, ids, expected, conflict) => {
      const resolve = createIdentityResolver({});
      const result = resolve(testCase('annotation', {}, [...ids]));
      expect(result.case_id).toBe(expected);
      expect(result.identity_conflict === true).toBe(conflict);
    },
  );

  it.each([
    ['scalar', 'not-an-object'],
    ['array', ['chromium']],
    ['secret key', { apiToken: 'qa-value-must-not-be-echoed' }],
    ['secret value', { credential: 'Bearer qa-value-must-not-be-echoed' }],
  ])(
    'refuses %s metadata params with one warning per project and no value echo',
    (_name, params) => {
      const warnings: WarningPayload[] = [];
      const resolve = createIdentityResolver({ warn: (warning) => warnings.push(warning) });
      const test = testCase('first', params);
      expect(resolve(test).variant_hash).toBeUndefined();
      expect(resolve({ ...test, id: 'second' }).variant_hash).toBeUndefined();
      expect(resolve({ ...test, id: 'third' }).variant_hash).toBeUndefined();
      expect(warnings.filter((warning) => warning.code === 'invalid_variant_params')).toHaveLength(
        1,
      );
      expect(JSON.stringify(warnings)).not.toContain('qa-value-must-not-be-echoed');
      expect(JSON.stringify(warnings)).not.toContain('not-an-object');
    },
  );

  it('keeps case and variant identity stable when a retry mutates annotations and metadata', () => {
    const resolve = createIdentityResolver({});
    const test = testCase('retry', { browser: 'chromium' });
    const first = resolve(test);
    const retry = { ...testCase('retry', { browser: 'firefox' }, [OTHER]) };
    expect(resolve(retry)).toEqual(first);
    expect(first.case_id).toBe(ID);
    expect(first.variant_hash).toMatch(/^sha256:[a-f0-9]{64}$/);
  });

  it.each(['x'.repeat(65), 'prod\u0001hidden', 'prod\nextra'])(
    'warns once for an invalid target env and falls back safely',
    (target) => {
      process.env.EVIDGATE_TARGET_ENV = target;
      const spool = mkdtempSync(join(tmpdir(), 'evidgate-identity-qa-'));
      dirs.push(spool);
      const reporter = new EvidgateReporter({
        spool_dir: spool,
        run_id: 'qa-run',
        producer_id: 'qa-producer',
        engineVersion: '1.63.0',
      });
      reporter.onBegin();
      reporter.onTestBegin(testCase('env'), { retry: 0 } as TestResult);
      void reporter.onEnd(fullResult);
      const events = envelopes(spool);
      expect(events.find((event) => event.type === 'test.started')?.ext.target_env).toBe(
        'chromium',
      );
      expect(
        events.filter(
          (event) => (event.ext.warn as WarningPayload | undefined)?.code === 'invalid_target_env',
        ),
      ).toHaveLength(1);
      expect(JSON.stringify(events)).not.toContain('hidden');
    },
  );
});
