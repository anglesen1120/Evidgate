// The built-in identity mapping: an `evidgate.id` annotation, the sidecar manifest, the
// conflict rule between them, the per-run duplicate warning and the per-test cache. Exercised
// directly through resolveIdentity and createIdentityResolver with a fake sidecar index.
import { describe, expect, it } from 'vitest';

import {
  CASE_A,
  CASE_B,
  fakeIndex,
  fakeTest,
  useIdentityResolveSpec,
} from './identity-resolve.testing.js';
import { createIdentityResolver, resolveIdentity, type WarningPayload } from './identity.js';

describe('resolveIdentity (annotation, sidecar, conflict)', () => {
  useIdentityResolveSpec();

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

  it('AC1: a nil-UUID annotation is refused and treated as absent', () => {
    const nil = '00000000-0000-0000-0000-000000000000';
    const warnings: WarningPayload[] = [];
    const identity = resolveIdentity(
      fakeTest({ annotations: [{ type: 'evidgate.id', description: nil }] }),
      { warn: (warning) => warnings.push(warning) },
    );
    expect(identity.case_id).toBeUndefined();
    expect(identity.source).toBeUndefined();
    expect(warnings.map((warning) => warning.code)).toEqual(['invalid_annotation']);

    // Treated as absent, not as a conflict: a sidecar mapping still resolves the test.
    const mapped = resolveIdentity(
      fakeTest({ annotations: [{ type: 'evidgate.id', description: nil }] }),
      { sidecar: fakeIndex({ 'tests/math.spec.ts': CASE_B }) },
    );
    expect(mapped.case_id).toBe(CASE_B);
    expect(mapped.source).toBe('sidecar');
    expect(mapped.identity_conflict).toBeUndefined();
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
