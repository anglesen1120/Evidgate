// The served `GET /v1/runs/{r}/decision` body, read here because the generated `Decision` type in
// `@evidgate/sdk` is stale: it still carries `evaluation_revision` and has no `status`, `missing`,
// `outcome`, hashes or `mode`. The decision-route task regenerates that type from the reconciled
// `contracts/openapi.yaml` schema, and this file is deleted in the same change; every field name and
// enum below is the wire contract's own spelling, never a local invention.
//
// One deliberate widening: `mode` accepts `shadow`, which the endpoint filters out (it serves
// `mode = 'live'` rows only). The receiver still has to recognise an off-contract shadow body to
// refuse it, so the literal is admitted here and never reaches the live slot.
//
// `unknown` arrives from the gateway: the seam cannot type the response with the stale SDK type, so
// the wire value is validated here instead of asserted. A value that does not match is `null`, and
// the caller renders the contract-invalid state — never a guessed verdict.
/* eslint-disable evidgate/no-hand-written-wire-dto -- the generated Decision type is stale until the decision-route task regenerates it (see above). */

/** One member's nullability follows the contract: pending bodies carry null, never a default. */
export interface DecisionBody {
  readonly status: 'pending' | 'evaluated';
  readonly decision: 'PASS' | 'FAIL' | 'INCONCLUSIVE' | null;
  readonly evaluation_id: string | null;
  readonly evidence_revision: number | null;
  readonly completeness: 'complete' | 'incomplete' | null;
  readonly outcome: 'all_passed' | 'has_failures' | 'no_results' | null;
  readonly missing: readonly string[] | null;
  readonly reasons: readonly string[] | null;
  readonly policy_hash: string | null;
  readonly manifest_hash: string | null;
  readonly evaluator_version: string | null;
  readonly mode: 'live' | 'shadow' | null;
  readonly url: string | null;
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

const DECISIONS: Record<string, true> = { PASS: true, FAIL: true, INCONCLUSIVE: true };
const COMPLETENESS: Record<string, true> = { complete: true, incomplete: true };
const OUTCOMES: Record<string, true> = {
  all_passed: true,
  has_failures: true,
  no_results: true,
};
const MODES: Record<string, true> = { live: true, shadow: true };

/**
 * The longest provenance identifier the surface renders as one line. A hash or version longer than
 * this is not a digest the contract can carry, so it is a malformed body rather than a long value.
 */
export const MAX_PROVENANCE_LENGTH = 512;

/** Provenance and identity members: a short digest, version or id string, never a document. */
const BOUNDED_MEMBERS = [
  'evaluation_id',
  'policy_hash',
  'manifest_hash',
  'evaluator_version',
] as const;

/** Every member is required: a receiver must see the member, even when it is null. */
const MEMBERS = [
  'status',
  'decision',
  'evaluation_id',
  'evidence_revision',
  'completeness',
  'outcome',
  'missing',
  'reasons',
  'policy_hash',
  'manifest_hash',
  'evaluator_version',
  'mode',
  'url',
] as const;

/**
 * The body when the served value has the contract's shape and value types, otherwise `null`.
 *
 * Shape and value bounds only: whether the members agree with each other (a pending body carrying a
 * decision, an evaluated body with a null member) is the mapper's question, because the answer is a
 * presentation state (`unavailable('contract')`), not a different wire type. Values are never
 * coerced — a numeric string in `evidence_revision` is a malformed body, not a revision — and a
 * revision below `1`, a blank hash or a hash-shaped string past `MAX_PROVENANCE_LENGTH` is
 * malformed for the same reason.
 */
export function readDecisionBody(value: unknown): DecisionBody | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null;
  const body = value as Record<string, unknown>;
  if (MEMBERS.some((member) => !Object.hasOwn(body, member))) return null;
  if (body['status'] !== 'pending' && body['status'] !== 'evaluated') return null;
  if (body['decision'] !== null && !allows(DECISIONS, body['decision'])) return null;
  if (body['completeness'] !== null && !allows(COMPLETENESS, body['completeness'])) return null;
  if (body['outcome'] !== null && !allows(OUTCOMES, body['outcome'])) return null;
  if (body['mode'] !== null && !allows(MODES, body['mode'])) return null;
  const revision = body['evidence_revision'];
  if (
    revision !== null &&
    (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 1)
  ) {
    return null;
  }
  for (const member of [
    'evaluation_id',
    'policy_hash',
    'manifest_hash',
    'evaluator_version',
    'url',
  ]) {
    const memberValue = body[member];
    if (memberValue === null) continue;
    if (typeof memberValue !== 'string') return null;
    if (
      BOUNDED_MEMBERS.some((bounded) => bounded === member) &&
      (memberValue.length === 0 ||
        memberValue.length > MAX_PROVENANCE_LENGTH ||
        memberValue.trim().length === 0)
    ) {
      return null;
    }
  }
  if (!isStringList(body['missing']) || !isStringList(body['reasons'])) return null;
  return value as DecisionBody;
}

function isStringList(value: unknown): value is readonly string[] | null {
  return value === null || (Array.isArray(value) && value.every((it) => typeof it === 'string'));
}

/** `Record` membership without inheriting `toString`/`constructor` as a permitted value. */
function allows(table: Record<string, true>, value: unknown): boolean {
  return typeof value === 'string' && Object.hasOwn(table, value);
}
