// 04.3.2-T1.2/T1.4: the built-in identity mapping — annotation `evidgate.id`, sidecar
// manifest, conflict rule, variant_hash, target_env, the warning channel and the onEnd
// exit code. The resolver is exercised both directly (createIdentityResolver with a fake
// sidecar index) and through the reporter (fake suite + injected SidecarFs), and every
// emitted envelope is validated against contracts/schemas/event-envelope.v1.json with the
// same Ajv 8 setup capability.test.ts uses.
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import {
  createIdentityResolver,
  PLACEHOLDER_EXECUTION_KEY,
  resolveIdentity,
  validTargetEnv,
  type WarningPayload,
} from './identity.js';
import {
  EvidgateReporter,
  MAX_WARNING_ENVELOPES,
  type EvidgateReporterOptions,
} from './reporter.js';
import type { SidecarFs, SidecarIndex, SidecarWarning } from './sidecar.js';

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

/** RFC 8785 of `{}` is `{}`, so the empty-params hash is fixed and computable here. */
const EMPTY_VARIANT_HASH = `sha256:${createHash('sha256').update('{}').digest('hex')}`;

const CASE_A = '11111111-1111-1111-1111-111111111111';
const CASE_B = '22222222-2222-2222-2222-222222222222';

const ENV_KEYS = ['EVIDGATE_TARGET_ENV', 'EVIDGATE_ALLOW_UNMAPPED'] as const;
const savedEnv: Record<string, string | undefined> = {};
const tempDirs: string[] = [];

beforeEach(() => {
  for (const key of ENV_KEYS) {
    savedEnv[key] = process.env[key];
    delete process.env[key];
  }
});

afterEach(() => {
  for (const key of ENV_KEYS) {
    const value = savedEnv[key];
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-identity-resolve-'));
  tempDirs.push(dir);
  return dir;
}

interface FakeProject {
  name: string;
  metadata?: unknown;
  use?: unknown;
}

interface FakeTestInput {
  id?: string;
  title?: string;
  file?: string;
  line?: number;
  column?: number;
  annotations?: Array<{ type: string; description?: string }>;
  project?: FakeProject;
}

function fakeTest(input: FakeTestInput = {}): TestCase {
  const project = input.project ?? { name: 'chromium' };
  return {
    id: input.id ?? 'test-1',
    title: input.title ?? 'adds two numbers',
    location: {
      file: input.file ?? 'tests/math.spec.ts',
      line: input.line ?? 3,
      column: input.column ?? 1,
    },
    annotations: input.annotations ?? [],
    parent: { project: () => project },
  } as unknown as TestCase;
}

function fakeResult(overrides: Record<string, unknown> = {}): TestResult {
  return {
    status: 'passed',
    duration: 1,
    retry: 0,
    steps: [],
    ...overrides,
  } as unknown as TestResult;
}

function fakeSuite(tests: TestCase[]): Suite {
  return { allTests: () => tests } as unknown as Suite;
}

/** A real temp directory holding the given files, so the sidecar loader's lstat sees them. */
function sidecarDir(files: Record<string, string>): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-sidecar-'));
  tempDirs.push(dir);
  for (const [name, content] of Object.entries(files)) writeFileSync(join(dir, name), content);
  return dir;
}

function fakeIndex(entries: Record<string, string>, warnings: SidecarWarning[] = []): SidecarIndex {
  return { lookup: (file: string) => entries[file], warnings };
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

function warningsOf(envelopes: EventEnvelope[]): WarningPayload[] {
  return envelopes
    .filter((item) => item.type === 'run.log' && item.ext.warn !== undefined)
    .map((item) => item.ext.warn as WarningPayload);
}

const fullResult = { status: 'passed' as const, startTime: new Date(), duration: 1 };

describe('resolveIdentity (annotation, sidecar, conflict)', () => {
  it('AC1: an evidgate.id annotation maps the test and lower-cases the UUID', () => {
    const identity = resolveIdentity(
      fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A.toUpperCase() }] }),
      {},
    );
    expect(identity.case_id).toBe(CASE_A);
    expect(identity.source).toBe('annotation');
    expect(identity.identity_conflict).toBeUndefined();
  });

  it('AC1: an invalid annotation UUID is ignored and reported once', () => {
    const warnings: WarningPayload[] = [];
    const identity = resolveIdentity(
      fakeTest({ annotations: [{ type: 'evidgate.id', description: 'not-a-uuid' }] }),
      { warn: (warning) => warnings.push(warning) },
    );
    expect(identity.case_id).toBeUndefined();
    expect(warnings.map((warning) => warning.code)).toEqual(['invalid_annotation']);
  });

  it('AC1: two different evidgate.id annotations on one test are a conflict', () => {
    const warnings: WarningPayload[] = [];
    const identity = resolveIdentity(
      fakeTest({
        annotations: [
          { type: 'evidgate.id', description: CASE_A },
          { type: 'evidgate.id', description: CASE_B },
        ],
      }),
      { warn: (warning) => warnings.push(warning) },
    );
    expect(identity.case_id).toBeUndefined();
    expect(identity.identity_conflict).toBe(true);
    expect(warnings.map((warning) => warning.code)).toEqual(['identity_conflict']);
  });

  it('AC2: annotation and sidecar disagreeing is a conflict, never a silent pick', () => {
    const warnings: WarningPayload[] = [];
    const identity = resolveIdentity(
      fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] }),
      {
        sidecar: fakeIndex({ 'tests/math.spec.ts': CASE_B }),
        warn: (warning) => warnings.push(warning),
      },
    );
    expect(identity.case_id).toBeUndefined();
    expect(identity.identity_conflict).toBe(true);
    expect(warnings[0]?.code).toBe('identity_conflict');
    expect(warnings[0]?.path).toBe('tests/math.spec.ts:3:1');
  });

  it('AC2: annotation and sidecar agreeing maps the test (no conflict)', () => {
    const identity = resolveIdentity(
      fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] }),
      { sidecar: fakeIndex({ 'tests/math.spec.ts': CASE_A }) },
    );
    expect(identity.case_id).toBe(CASE_A);
    expect(identity.source).toBe('annotation');
    expect(identity.identity_conflict).toBeUndefined();
  });

  it('maps a test from the sidecar alone', () => {
    const identity = resolveIdentity(fakeTest(), {
      sidecar: fakeIndex({ 'tests/math.spec.ts': CASE_B }),
    });
    expect(identity.case_id).toBe(CASE_B);
    expect(identity.source).toBe('sidecar');
  });

  it('leaves an unannotated, unmapped test without a case id', () => {
    const identity = resolveIdentity(fakeTest(), {});
    expect(identity.case_id).toBeUndefined();
    expect(identity.source).toBeUndefined();
    expect(identity.identity_conflict).toBeUndefined();
  });

  it('AC1: a duplicate case id across two tests warns once, naming both locations', () => {
    const warnings: WarningPayload[] = [];
    const resolver = createIdentityResolver({ warn: (warning) => warnings.push(warning) });
    resolver(fakeTest({ id: 't1', annotations: [{ type: 'evidgate.id', description: CASE_A }] }));
    resolver(
      fakeTest({
        id: 't2',
        file: 'tests/other.spec.ts',
        line: 9,
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
      }),
    );
    expect(warnings.map((warning) => warning.code)).toEqual(['duplicate_case_id']);
    expect(warnings[0]?.message).toContain('tests/math.spec.ts:3:1');
    expect(warnings[0]?.message).toContain('tests/other.spec.ts:9:1');
  });

  it('§6: a conflicted test is never re-mapped within the run (cached by test id)', () => {
    const warnings: WarningPayload[] = [];
    const resolver = createIdentityResolver({
      sidecar: fakeIndex({ 'tests/math.spec.ts': CASE_B }),
      warn: (warning) => warnings.push(warning),
    });
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    const first = resolver(test);
    const second = resolver(test);
    expect(first).toBe(second);
    expect(warnings.filter((warning) => warning.code === 'identity_conflict')).toHaveLength(1);
  });
});

describe('variant_hash (server VariantNormalizer semantics)', () => {
  it('AC3: hashes the empty params of a project without declared params', () => {
    const identity = resolveIdentity(fakeTest(), {});
    expect(identity.variant_hash).toBe(EMPTY_VARIANT_HASH);
  });

  it('AC3: is identical across tests, retries and shards of one project', () => {
    const project: FakeProject = {
      name: 'chromium',
      metadata: { evidgate: { params: { browser: 'chromium', mode: 'full' } } },
    };
    const resolver = createIdentityResolver({});
    const first = resolver(fakeTest({ id: 't1', project }));
    const retry = resolver(fakeTest({ id: 't1', project }));
    const shard = resolver(fakeTest({ id: 't2', file: 'tests/b.spec.ts', project }));
    expect(first.variant_hash).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(retry.variant_hash).toBe(first.variant_hash);
    expect(shard.variant_hash).toBe(first.variant_hash);
  });

  it('AC3: params come from metadata.evidgate.params only, never from `use`', () => {
    const chromium = resolveIdentity(
      fakeTest({ project: { name: 'chromium', use: { browserName: 'chromium' } } }),
      {},
    );
    const firefox = resolveIdentity(
      fakeTest({ project: { name: 'chromium', use: { browserName: 'firefox' } } }),
      {},
    );
    expect(chromium.variant_hash).toBe(EMPTY_VARIANT_HASH);
    expect(firefox.variant_hash).toBe(EMPTY_VARIANT_HASH);

    const declared = resolveIdentity(
      fakeTest({
        project: { name: 'chromium', metadata: { evidgate: { params: { browser: 'firefox' } } } },
      }),
      {},
    );
    expect(declared.variant_hash).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(declared.variant_hash).not.toBe(EMPTY_VARIANT_HASH);
  });

  it('refuses a secret-shaped params set without echoing the value, once per project', () => {
    const warnings: WarningPayload[] = [];
    const project: FakeProject = {
      name: 'chromium',
      metadata: { evidgate: { params: { api_token: 'super-secret-value' } } },
    };
    const resolver = createIdentityResolver({ warn: (warning) => warnings.push(warning) });
    const first = resolver(fakeTest({ id: 't1', project }));
    const second = resolver(fakeTest({ id: 't2', file: 'tests/b.spec.ts', project }));
    expect(first.variant_hash).toBeUndefined();
    expect(second.variant_hash).toBeUndefined();
    const invalid = warnings.filter((warning) => warning.code === 'invalid_variant_params');
    expect(invalid).toHaveLength(1);
    expect(invalid[0]?.message).not.toContain('super-secret-value');
  });

  it('does not change identity for Unicode titles or paths', () => {
    const project: FakeProject = { name: 'chromium' };
    const nfc = resolveIdentity(
      fakeTest({
        id: 't1',
        title: 'renders café 🧪',
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
        project,
      }),
      {},
    );
    const nfd = resolveIdentity(
      fakeTest({
        id: 't2',
        title: 'renders cafe\u0301 🧪',
        file: 'tests/ünïcode.spec.ts',
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
        project,
      }),
      {},
    );
    expect(nfc.case_id).toBe(CASE_A);
    expect(nfd.case_id).toBe(CASE_A);
    expect(nfd.variant_hash).toBe(nfc.variant_hash);
  });
});

describe('target_env', () => {
  it('defaults to the Playwright project name', () => {
    expect(resolveIdentity(fakeTest(), {}).target_env).toBe('chromium');
  });

  it('omits an invalid project name', () => {
    expect(
      resolveIdentity(fakeTest({ project: { name: 'Chromium Desktop' } }), {}).target_env,
    ).toBe(undefined);
  });

  it('prefers the ctor-validated EVIDGATE_TARGET_ENV', () => {
    const identity = resolveIdentity(fakeTest(), { targetEnv: 'staging-eu' });
    expect(identity.target_env).toBe('staging-eu');
  });

  it('validates the manifest targetEnv shape', () => {
    expect(validTargetEnv('prod')).toBe('prod');
    expect(validTargetEnv('staging-eu_2')).toBe('staging-eu_2');
    expect(validTargetEnv('')).toBeUndefined();
    expect(validTargetEnv('Prod')).toBeUndefined();
    expect(validTargetEnv('-leading')).toBeUndefined();
    expect(validTargetEnv('a'.repeat(65))).toBeUndefined();
  });
});

describe('reporter identity wiring', () => {
  it('AC1/AC4: emits ext.case_id/variant_hash/target_env on test.started, test.finished and test.step', () => {
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(
      test,
      fakeResult({ steps: [{ title: 'step', category: 'test.step', duration: 2, steps: [] }] }),
    );
    reporter.onEnd(fullResult);

    const envelopes = readEnvelopes(spool);
    for (const type of ['test.started', 'test.finished', 'test.step']) {
      const envelope = envelopes.find((item) => item.type === type);
      expect(envelope?.ext.case_id, type).toBe(CASE_A);
      expect(envelope?.ext.variant_hash, type).toBe(EMPTY_VARIANT_HASH);
      expect(envelope?.ext.target_env, type).toBe('chromium');
      expect(envelope?.execution_key, type).toBe(PLACEHOLDER_EXECUTION_KEY);
    }
  });

  it('AC1: a duplicate case id across two tests emits one warning naming both locations', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(
      fakeTest({ id: 't1', annotations: [{ type: 'evidgate.id', description: CASE_A }] }),
      fakeResult(),
    );
    reporter.onTestBegin(
      fakeTest({
        id: 't2',
        file: 'tests/other.spec.ts',
        line: 9,
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
      }),
      fakeResult(),
    );
    reporter.onEnd(fullResult);

    const duplicates = warningsOf(readEnvelopes(spool)).filter(
      (warning) => warning.code === 'duplicate_case_id',
    );
    expect(duplicates).toHaveLength(1);
    expect(duplicates[0]?.message).toContain('tests/math.spec.ts:3:1');
    expect(duplicates[0]?.message).toContain('tests/other.spec.ts:9:1');
  });

  it('AC2: a conflict emits identity_conflict, no case_id, and onEnd fails', () => {
    const dir = sidecarDir({
      'math.spec.ts': '',
      'evidgate.cases.yaml': `version: 1\ncases:\n  - file: math.spec.ts\n    evidgate.id: ${CASE_B}\n`,
    });
    const { reporter, spool } = makeReporter();
    const test = fakeTest({
      id: 't1',
      file: join(dir, 'math.spec.ts'),
      annotations: [{ type: 'evidgate.id', description: CASE_A }],
    });
    reporter.onBegin(undefined, fakeSuite([test]));
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(test, fakeResult());

    const finished = readEnvelopes(spool).find((item) => item.type === 'test.finished');
    expect(finished?.ext.identity_conflict).toBe(true);
    expect(finished?.ext.case_id).toBeUndefined();

    expect(reporter.onEnd(fullResult)).toEqual({ status: 'failed' });
  });

  it('AC2: EVIDGATE_ALLOW_UNMAPPED=1 keeps an unmapped run from failing', () => {
    process.env.EVIDGATE_ALLOW_UNMAPPED = '1';
    const { reporter } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    expect(reporter.onEnd(fullResult)).toBeUndefined();
  });

  it('AC2: an unmapped-only run fails without the flag', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    expect(reporter.onEnd(fullResult)).toEqual({ status: 'failed' });

    const summary = warningsOf(readEnvelopes(spool)).find((warning) => warning.code === 'unmapped');
    expect(summary?.count).toBe(1);
  });

  it('AC2: a fully mapped run does not fail', () => {
    const { reporter } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    expect(reporter.onEnd(fullResult)).toBeUndefined();
  });

  it('reads EVIDGATE_TARGET_ENV in the ctor and warns once when it is invalid', () => {
    process.env.EVIDGATE_TARGET_ENV = '  staging-eu  ';
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    reporter.onEnd(fullResult);
    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.ext.target_env).toBe('staging-eu');
  });

  it('warns invalid_target_env and falls back to the project name', () => {
    process.env.EVIDGATE_TARGET_ENV = 'Not Valid';
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    reporter.onEnd(fullResult);
    const envelopes = readEnvelopes(spool);
    expect(envelopes.find((item) => item.type === 'test.started')?.ext.target_env).toBe('chromium');
    expect(warningsOf(envelopes).map((warning) => warning.code)).toContain('invalid_target_env');
  });

  it('builds the sidecar index at onBegin from the suite test files', () => {
    const dir = sidecarDir({
      'a.spec.ts': '',
      'evidgate.cases.yaml': `version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: ${CASE_B}\n`,
    });
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ id: 't1', file: join(dir, 'a.spec.ts') });
    reporter.onBegin(undefined, fakeSuite([test]));
    reporter.onTestBegin(test, fakeResult());
    reporter.onEnd(fullResult);

    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.ext.case_id).toBe(CASE_B);
  });

  it('reads the sidecar through the injected SidecarFs seam', () => {
    const dir = sidecarDir({ 'a.spec.ts': '', 'evidgate.cases.yaml': 'version: 1\ncases: []\n' });
    const fs: SidecarFs = {
      readFile: (path) =>
        path === join(dir, 'evidgate.cases.yaml')
          ? Buffer.from(`version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: ${CASE_B}\n`)
          : undefined,
    };
    const { reporter, spool } = makeReporter({ sidecarFs: fs });
    const test = fakeTest({ id: 't1', file: join(dir, 'a.spec.ts') });
    reporter.onBegin(undefined, fakeSuite([test]));
    reporter.onTestBegin(test, fakeResult());
    reporter.onEnd(fullResult);

    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.ext.case_id).toBe(CASE_B);
  });

  it('emits sidecar warnings as run.log envelopes at onBegin', () => {
    const dir = sidecarDir({ 'a.spec.ts': '', 'evidgate.cases.yaml': 'version: 2\ncases: []\n' });
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ id: 't1', file: join(dir, 'a.spec.ts') });
    reporter.onBegin(undefined, fakeSuite([test]));
    reporter.onEnd(fullResult);

    const envelopes = readEnvelopes(spool);
    expect(envelopes[0]?.ext.capability).toBeDefined();
    const codes = warningsOf(envelopes).map((warning) => warning.code);
    expect(codes).toContain('invalid_sidecar');
  });

  it('the injected resolver still wins over the built-in mapping', () => {
    const resolved = 'a'.repeat(64);
    const { reporter, spool } = makeReporter({
      identity: () => ({ execution_key: resolved, case_id: CASE_B }),
    });
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    reporter.onEnd(fullResult);

    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.execution_key).toBe(resolved);
    expect(started?.ext.case_id).toBe(CASE_B);
  });

  it('caps warning envelopes and summarises the overflow once', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    const total = MAX_WARNING_ENVELOPES + 50;
    for (let index = 0; index < total; index += 1) {
      reporter.onTestBegin(
        fakeTest({
          id: `t${index}`,
          file: `tests/t${index}.spec.ts`,
          annotations: [{ type: 'evidgate.id', description: CASE_A }],
        }),
        fakeResult(),
      );
    }
    reporter.onEnd(fullResult);

    const warnings = warningsOf(readEnvelopes(spool));
    expect(warnings.filter((warning) => warning.code === 'duplicate_case_id')).toHaveLength(
      MAX_WARNING_ENVELOPES,
    );
    const truncated = warnings.find((warning) => warning.code === 'warnings_truncated');
    expect(truncated?.count).toBe(total - 1 - MAX_WARNING_ENVELOPES);
  });

  it('every emitted envelope validates against the envelope schema', () => {
    const { reporter, spool } = makeReporter();
    const mapped = fakeTest({
      id: 't1',
      annotations: [{ type: 'evidgate.id', description: CASE_A }],
    });
    const duplicate = fakeTest({
      id: 't2',
      file: 'tests/other.spec.ts',
      annotations: [{ type: 'evidgate.id', description: CASE_A }],
    });
    const invalid = fakeTest({
      id: 't3',
      file: 'tests/third.spec.ts',
      annotations: [{ type: 'evidgate.id', description: 'not-a-uuid' }],
    });
    const unmapped = fakeTest({ id: 't4', file: 'tests/fourth.spec.ts' });
    reporter.onBegin();
    for (const test of [mapped, duplicate, invalid, unmapped]) {
      reporter.onTestBegin(test, fakeResult());
      reporter.onTestEnd(test, fakeResult());
    }
    reporter.onEnd(fullResult);

    const envelopes = readEnvelopes(spool);
    expect(envelopes.length).toBeGreaterThan(0);
    for (const envelope of envelopes) {
      expect(validateEnvelope(envelope), JSON.stringify(validateEnvelope.errors)).toBe(true);
    }
  });
});
