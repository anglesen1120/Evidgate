// The run detail's decision corpus (09.3.1-T5), served by the dev backend from `FIXTURE_CATALOG`
// instead of a `fixtures/web-api/<name>/input.json` directory until 07.5.2-T1 reconciles the
// `Decision` schema: `contracts/openapi.yaml` still declares `reasons` as a non-nullable array, so a
// `pending` body — which needs every evaluation-derived member `null`, `reasons` included — cannot
// live in the corpus without either failing the 09.1.1-T12.2 contract check or dropping that rule.
// 07.5.2-T1 moves these documents back into `fixtures/web-api/` once the schema carries the 07.5.2
// §5.1 body; nothing else about them changes.
//
// Each body is typed by `features/runs/data/decision-body.ts` (the web-local 07.5.2 §5.1 body, which
// the stale SDK `Decision` cannot express), so a renamed member or a widened enum fails here at
// compile time rather than at the first fixture-backed screen.
// eslint-disable-next-line evidgate/no-feature-cross-import -- dev-only fixture corpus, absent from every production bundle (the `fixtures` build configuration is its only importer); the body type it must satisfy is the web-local decision declaration.
import type { DecisionBody } from '../../features/runs/data/decision-body';

/** `pending`: `status` alone, every evaluation-derived member `null` (00 §9.3). */
export const PENDING_DECISION_BODY = {
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
  url: null,
} satisfies DecisionBody;

/** The evaluated body of the incomplete revision: a FAIL that names the missing shard. */
export const FAILED_DECISION_BODY = {
  status: 'evaluated',
  decision: 'FAIL',
  evaluation_id: '018f0000-0000-7000-8000-0000000e0002',
  evidence_revision: 2,
  completeness: 'incomplete',
  outcome: 'has_failures',
  missing: ['missing_shard'],
  reasons: ['missing_shard'],
  policy_hash: 'sha256:018f00000000000000000000000000000000000000000000000000000000a004',
  manifest_hash: 'sha256:018f000000000000000000000000000000000000000000000000000000000000',
  evaluator_version: 'evidgate-evaluator/1.4.0',
  mode: 'live',
  url: '/v1/runs/018f0000-0000-7000-8000-000000000071/decision?evidence_revision=2',
} satisfies DecisionBody;

/** A complete evaluated PASS with no reasons. */
export const PASSED_DECISION_BODY = {
  status: 'evaluated',
  decision: 'PASS',
  evaluation_id: '018f0000-0000-7000-8000-0000000e0001',
  evidence_revision: 1,
  completeness: 'complete',
  outcome: 'all_passed',
  missing: [],
  reasons: [],
  policy_hash: 'sha256:018f00000000000000000000000000000000000000000000000000000000a004',
  manifest_hash: 'sha256:018f000000000000000000000000000000000000000000000000000000000000',
  evaluator_version: 'evidgate-evaluator/1.4.0',
  mode: 'live',
  url: '/v1/runs/018f0000-0000-7000-8000-000000000071/decision?evidence_revision=1',
} satisfies DecisionBody;

/**
 * The off-contract body the endpoint filters out: a `mode: shadow` PASS. The receiver must refuse
 * it, so the corpus serves one rather than assuming the server never does (T5.2).
 */
export const SHADOW_DECISION_BODY = {
  status: 'evaluated',
  decision: 'PASS',
  evaluation_id: '018f0000-0000-7000-8000-0000000e0003',
  evidence_revision: 3,
  completeness: 'complete',
  outcome: 'all_passed',
  missing: [],
  reasons: [],
  policy_hash: 'sha256:018f00000000000000000000000000000000000000000000000000000000a004',
  manifest_hash: 'sha256:018f000000000000000000000000000000000000000000000000000000000000',
  evaluator_version: 'evidgate-evaluator/1.4.0-shadow',
  mode: 'shadow',
  url: '/v1/runs/018f0000-0000-7000-8000-000000000071/decision?mode=shadow',
} satisfies DecisionBody;

/** The `503` answer: the shared 00 §9.2 envelope with the `Retry-After` the region renders. */
export const DECISION_UNAVAILABLE_DOCUMENT = {
  operation_id: 'getRunDecision',
  variant: 'unavailable',
  status: 503,
  headers: {
    'X-Request-Id': '018f0000-0000-7000-8000-00000000dc05',
    'Retry-After': '5',
  },
  latency_ms: 0,
  body: {
    error: {
      code: 'unavailable',
      message: 'the decision service is unavailable for this request',
      request_id: '018f0000-0000-7000-8000-00000000dc05',
      retryable: true,
      details: [],
    },
  },
};

/** Every decision document, keyed by the catalog row name the dev backend serves. */
export const DECISION_DOCUMENTS = {
  'run-decision': {
    operation_id: 'getRunDecision',
    variant: 'ok',
    status: 200,
    headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000dc01' },
    latency_ms: 0,
    body: PENDING_DECISION_BODY,
  },
  'run-decision-evaluated-fail-missing-shard': {
    operation_id: 'getRunDecision',
    variant: 'evaluated-fail-missing-shard',
    status: 200,
    headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000dc02' },
    latency_ms: 0,
    body: FAILED_DECISION_BODY,
  },
  'run-decision-evaluated-pass': {
    operation_id: 'getRunDecision',
    variant: 'evaluated-pass',
    status: 200,
    headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000dc03' },
    latency_ms: 0,
    body: PASSED_DECISION_BODY,
  },
  'run-decision-shadow-refused': {
    operation_id: 'getRunDecision',
    variant: 'shadow-refused',
    status: 200,
    headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000dc04' },
    latency_ms: 0,
    body: SHADOW_DECISION_BODY,
  },
  'run-decision-unavailable': DECISION_UNAVAILABLE_DOCUMENT,
} as const;
