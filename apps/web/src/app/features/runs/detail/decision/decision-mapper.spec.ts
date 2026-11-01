import { readDecisionBody } from '../../data/decision-body';
import { toDecisionCandidate } from './decision-mapper';

/**
 * The served body a real 200 carries for one evaluated revision. Every case below starts from it,
 * so a rejection is attributable to the one member the case changes. Values stay `unknown`-typed:
 * the pipeline validates a wire value, so a spec may feed it a malformed one on purpose.
 */
function evaluated(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    status: 'evaluated',
    decision: 'FAIL',
    evaluation_id: '6f1d0f43-6dd4-4f43-9d7a-2f5a6f3f1a2b',
    evidence_revision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: ['missing_shard'],
    reasons: ['critical_not_executed'],
    policy_hash: 'sha256:policy',
    manifest_hash: 'sha256:manifest',
    evaluator_version: '1.4.0',
    mode: 'live',
    url: 'https://evidgate.test/projects/p1/runs/r1',
    ...overrides,
  };
}

/** The pending body: `status` alone is set, and every evaluation-derived member is null. */
function pending(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    status: 'pending',
    decision: null,
    evaluation_id: null,
    evidence_revision: null,
    completeness: null,
    outcome: null,
    missing: null,
    reasons: null,
    policy_hash: null,
    manifest_hash: null,
    evaluator_version: null,
    mode: null,
    url: 'https://evidgate.test/projects/p1/runs/r1',
    ...overrides,
  };
}

/** The pipeline the store uses: validate the served value, then map it into a slot. */
function map(raw: unknown) {
  return toDecisionCandidate(readDecisionBody(raw));
}

const CONTRACT_INVALID = { kind: 'unavailable', cause: 'contract' };

describe('decision body and view mapper', () => {
  it('maps an evaluated body member for member, in server order and with duplicates kept', () => {
    const body = evaluated({
      missing: ['missing_shard', 'count_mismatch', 'missing_shard'],
      reasons: ['critical_not_executed', 'expected_count_zero', 'critical_not_executed'],
    });
    expect(map(body)).toEqual({
      kind: 'evaluated',
      decision: 'FAIL',
      evidenceRevision: 4,
      completeness: 'incomplete',
      outcome: 'has_failures',
      missing: ['missing_shard', 'count_mismatch', 'missing_shard'],
      reasons: ['critical_not_executed', 'expected_count_zero', 'critical_not_executed'],
      policyHash: 'sha256:policy',
      manifestHash: 'sha256:manifest',
      evaluatorVersion: '1.4.0',
      mode: 'live',
    });
  });

  it('keeps an unknown missing literal as the literal, never a ninth code or an empty list', () => {
    const view = map(evaluated({ missing: ['missing_shard', 'future_code'] }));
    expect(view).toMatchObject({ kind: 'evaluated', missing: ['missing_shard', 'future_code'] });
  });

  it('maps a pending body to pending with no verdict and no fabricated members', () => {
    expect(map(pending())).toEqual({ kind: 'pending' });
  });

  it('treats a shadow body as a candidate that may not fill the live slot', () => {
    expect(map(evaluated({ mode: 'shadow' }))).toEqual({ kind: 'shadow' });
  });

  it.each([
    ['unknown decision', { decision: 'SKIPPED' }],
    ['lower-case decision', { decision: 'pass' }],
    ['unknown mode', { mode: 'recorded' }],
    ['unknown completeness', { completeness: 'partial' }],
    ['unknown outcome', { outcome: 'mostly_passed' }],
    ['unknown status', { status: 'done' }],
  ])('refuses an evaluated body with an %s as the contract-invalid state', (_name, override) => {
    expect(map({ ...evaluated(), ...override })).toEqual(CONTRACT_INVALID);
  });

  it.each([
    ['missing is null', { missing: null }],
    ['reasons is null', { reasons: null }],
    ['evidence_revision is null', { evidence_revision: null }],
    ['completeness is null', { completeness: null }],
    ['outcome is null', { outcome: null }],
    ['mode is null', { mode: null }],
    ['decision is null', { decision: null }],
    ['evaluation_id is null', { evaluation_id: null }],
    ['policy_hash is empty', { policy_hash: '' }],
    ['manifest_hash is absent', { manifest_hash: undefined }],
  ])('refuses an evaluated body whose %s', (_name, override) => {
    expect(map({ ...evaluated(), ...override })).toEqual(CONTRACT_INVALID);
  });

  it.each([0, -1, 3.5, '4', 'three'])(
    'refuses the evidence revision %s without coercing it',
    (revision) => {
      expect(map(evaluated({ evidence_revision: revision }))).toEqual(CONTRACT_INVALID);
    },
  );

  it('refuses a pending body that carries an evaluation-derived member', () => {
    expect(map(pending({ decision: 'PASS' }))).toEqual(CONTRACT_INVALID);
    expect(map(pending({ missing: [] }))).toEqual(CONTRACT_INVALID);
    expect(map(pending({ mode: 'live' }))).toEqual(CONTRACT_INVALID);
  });

  it('refuses a body that omits a required member, even a null one', () => {
    const withoutUrl = evaluated();
    delete withoutUrl['url'];
    expect(map(withoutUrl)).toEqual(CONTRACT_INVALID);
  });

  it.each([
    ['a null value', null],
    ['an array', []],
    ['a string', 'PASS'],
    ['a number', 3],
  ])('refuses %s as the whole body', (_name, value) => {
    expect(map(value)).toEqual(CONTRACT_INVALID);
  });

  it('refuses a body whose member types are wrong but whose names are right', () => {
    expect(map(evaluated({ missing: 'missing_shard' }))).toEqual(CONTRACT_INVALID);
    expect(map(evaluated({ reasons: [1, 2] }))).toEqual(CONTRACT_INVALID);
    expect(map(evaluated({ policy_hash: 7 }))).toEqual(CONTRACT_INVALID);
  });

  it('accepts the documented INCONCLUSIVE and all_passed/complete combination verbatim', () => {
    expect(
      map(
        evaluated({
          decision: 'INCONCLUSIVE',
          outcome: 'no_results',
          completeness: 'complete',
          missing: [],
          reasons: [],
        }),
      ),
    ).toMatchObject({ kind: 'evaluated', decision: 'INCONCLUSIVE', outcome: 'no_results' });
  });
});
