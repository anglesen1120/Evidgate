// The run detail's evidence revision corpus, served by the dev backend from `FIXTURE_CATALOG`
// instead of a `fixtures/web-api/<name>/input.json` directory until the decision reads are
// reconciled: the pages are the same three-elements-newest-first list a server would answer, and
// they move back into the corpus with the decision documents of `./decision-fixtures.ts`.
//
// Every page is typed by the generated SDK `EvidenceRevisionPage`, so the corpus cannot drift from
// `GET /v1/runs/{run_id}/evidence-revisions`: the row members, their nullability (`evaluation_id`)
// and the closed `missing` vocabulary are the generated type's, not a local restatement.
import type { EvidenceRevisionPage } from '@evidgate/sdk';

/**
 * Three frozen revisions, newest first: `3` complete with no live evaluation yet, `2` incomplete
 * with the missing-shard reason (never a PASS), `1` complete and evaluated.
 */
export const EVIDENCE_REVISIONS_PAGE = {
  items: [
    {
      revision_no: 3,
      created_at: '2026-10-31T10:30:00Z',
      completeness: 'complete',
      missing: [],
      evaluation_id: null,
    },
    {
      revision_no: 2,
      created_at: '2026-10-31T09:12:00Z',
      completeness: 'incomplete',
      missing: ['missing_shard'],
      evaluation_id: '018f0000-0000-7000-8000-0000000e0002',
    },
    {
      revision_no: 1,
      created_at: '2026-10-31T08:00:00Z',
      completeness: 'complete',
      missing: [],
      evaluation_id: '018f0000-0000-7000-8000-0000000e0001',
    },
  ],
  next_cursor: null,
} satisfies EvidenceRevisionPage;

/** A run with no frozen revision at all: an empty page, never a 404. */
export const EMPTY_EVIDENCE_REVISIONS_PAGE = {
  items: [],
  next_cursor: null,
} satisfies EvidenceRevisionPage;

/** Every evidence-revision document, keyed by the catalog row name the dev backend serves. */
export const EVIDENCE_REVISION_DOCUMENTS = {
  'run-evidence-revisions': {
    operation_id: 'listEvidenceRevisions',
    variant: 'ok',
    status: 200,
    headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000ee01' },
    latency_ms: 0,
    body: EVIDENCE_REVISIONS_PAGE,
  },
  'run-evidence-revisions-empty': {
    operation_id: 'listEvidenceRevisions',
    variant: 'empty',
    status: 200,
    headers: { 'X-Request-Id': '018f0000-0000-7000-8000-00000000ee02' },
    latency_ms: 0,
    body: EMPTY_EVIDENCE_REVISIONS_PAGE,
  },
  'run-evidence-revisions-unavailable': {
    operation_id: 'listEvidenceRevisions',
    variant: 'unavailable',
    status: 503,
    headers: {
      'X-Request-Id': '018f0000-0000-7000-8000-00000000ee03',
      'Retry-After': '5',
    },
    latency_ms: 0,
    body: {
      error: {
        code: 'unavailable',
        message: 'the evidence service is unavailable for this request',
        request_id: '018f0000-0000-7000-8000-00000000ee03',
        retryable: true,
        details: [],
      },
    },
  },
} as const;
