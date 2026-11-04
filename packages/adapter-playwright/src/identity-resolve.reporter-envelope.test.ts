// Reporter identity wiring: the case_id/variant_hash/target_env fields on test.started,
// test.finished and test.step, the sidecar index built at onBegin, the injected resolver and
// SidecarFs seams, and every emitted envelope validated against the envelope schema.
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  CASE_A,
  CASE_B,
  EMPTY_VARIANT_HASH,
  fakeResult,
  fakeSuite,
  fakeTest,
  fullResult,
  makeReporter,
  readEnvelopes,
  sidecarDir,
  useIdentityResolveSpec,
  validateEnvelope,
  warningsOf,
} from './identity-resolve.testing.js';
import { PLACEHOLDER_EXECUTION_KEY } from './identity.js';
import type { SidecarFs } from './sidecar.js';

describe('reporter identity wiring', () => {
  useIdentityResolveSpec();

  it('AC1/AC4: emits ext.case_id/variant_hash/target_env on test.started, test.finished and test.step', () => {
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(
      test,
      fakeResult({ steps: [{ title: 'step', category: 'test.step', duration: 2, steps: [] }] }),
    );
    void reporter.onEnd(fullResult);

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
    void reporter.onEnd(fullResult);

    const duplicates = warningsOf(readEnvelopes(spool)).filter(
      (warning) => warning.code === 'duplicate_case_id',
    );
    expect(duplicates).toHaveLength(1);
    expect(duplicates[0]?.message).toContain('tests/math.spec.ts:3:1');
    expect(duplicates[0]?.message).toContain('tests/other.spec.ts:9:1');
  });

  it('reads EVIDGATE_TARGET_ENV in the ctor and warns once when it is invalid', () => {
    process.env.EVIDGATE_TARGET_ENV = 'Not Valid';
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    void reporter.onEnd(fullResult);
    const envelopes = readEnvelopes(spool);
    expect(envelopes.find((item) => item.type === 'test.started')?.ext.target_env).toBe('chromium');
    expect(
      warningsOf(envelopes).filter((warning) => warning.code === 'invalid_target_env'),
    ).toHaveLength(1);
  });

  it('trims a valid EVIDGATE_TARGET_ENV read in the ctor', () => {
    process.env.EVIDGATE_TARGET_ENV = '  staging-eu  ';
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    void reporter.onEnd(fullResult);
    const envelopes = readEnvelopes(spool);
    expect(envelopes.find((item) => item.type === 'test.started')?.ext.target_env).toBe(
      'staging-eu',
    );
    expect(warningsOf(envelopes).map((warning) => warning.code)).not.toContain(
      'invalid_target_env',
    );
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
    void reporter.onEnd(fullResult);

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
    void reporter.onEnd(fullResult);

    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.ext.case_id).toBe(CASE_B);
  });

  it('emits sidecar warnings as run.log envelopes at onBegin', () => {
    const dir = sidecarDir({ 'a.spec.ts': '', 'evidgate.cases.yaml': 'version: 2\ncases: []\n' });
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ id: 't1', file: join(dir, 'a.spec.ts') });
    reporter.onBegin(undefined, fakeSuite([test]));
    void reporter.onEnd(fullResult);

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
    void reporter.onEnd(fullResult);

    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.execution_key).toBe(resolved);
    expect(started?.ext.case_id).toBe(CASE_B);
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
    void reporter.onEnd(fullResult);

    const envelopes = readEnvelopes(spool);
    expect(envelopes.length).toBeGreaterThan(0);
    for (const envelope of envelopes) {
      expect(validateEnvelope(envelope), JSON.stringify(validateEnvelope.errors)).toBe(true);
    }
  });
});
