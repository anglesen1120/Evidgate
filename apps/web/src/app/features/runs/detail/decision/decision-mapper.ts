// Maps the served decision body to what the run-detail surface can render. Two jobs, both
// presentation: refuse a body whose members contradict each other, and say which slot the body is
// allowed to occupy. It never computes a verdict, a completeness value or an outcome — every value
// on the view model is a member of one accepted body.
//
// A `shadow` body is a candidate, not a state: the live slot keeps whatever it already accepted
// (the endpoint filters shadow rows out, so such a body is off-contract whatever it says), and an
// unknown `missing` literal is kept as a literal for the caller to render, never dropped or mapped
// onto one of the eight codes.

import type { DecisionBody } from '../../data/decision-body';
import type { DecisionViewModel } from './decision-view-model';

/** A served body that may not fill the live slot; the caller keeps its accepted snapshot. */
export interface ShadowCandidate {
  readonly kind: 'shadow';
}

const CONTRACT_DEFECT: DecisionViewModel = { kind: 'unavailable', cause: 'contract' };

/**
 * The view the body is allowed to produce: `pending`, an `evaluated` body, the contract-invalid
 * state, or the shadow candidate the caller refuses.
 *
 * `pending` needs EVERY evaluation-derived member null, so a pending body that carries a decision
 * is contradictory and renders unavailable; an `evaluated` body needs them all present, so a null
 * `missing` is unavailable rather than the empty list, which would read as "nothing is missing".
 */
export function toDecisionCandidate(
  body: DecisionBody | null,
): DecisionViewModel | ShadowCandidate {
  if (body === null) return CONTRACT_DEFECT;
  if (body.mode === 'shadow') return { kind: 'shadow' };
  if (body.status === 'pending') {
    const everyMemberNull =
      body.decision === null &&
      body.evaluation_id === null &&
      body.evidence_revision === null &&
      body.completeness === null &&
      body.outcome === null &&
      body.missing === null &&
      body.reasons === null &&
      body.policy_hash === null &&
      body.manifest_hash === null &&
      body.evaluator_version === null &&
      body.mode === null;
    return everyMemberNull ? { kind: 'pending' } : CONTRACT_DEFECT;
  }
  if (
    body.mode !== 'live' ||
    body.decision === null ||
    body.evaluation_id === null ||
    body.evidence_revision === null ||
    body.evidence_revision < 1 ||
    body.completeness === null ||
    body.outcome === null ||
    body.missing === null ||
    body.reasons === null ||
    !body.policy_hash ||
    !body.manifest_hash ||
    !body.evaluator_version
  ) {
    return CONTRACT_DEFECT;
  }
  return {
    kind: 'evaluated',
    decision: body.decision,
    evidenceRevision: body.evidence_revision,
    completeness: body.completeness,
    outcome: body.outcome,
    missing: body.missing,
    reasons: body.reasons,
    policyHash: body.policy_hash,
    manifestHash: body.manifest_hash,
    evaluatorVersion: body.evaluator_version,
    mode: 'live',
  };
}
