// 09.1.1-T3.1 (00 §9.2, ADR-0008 decision (a)): pins the error table row by row. Every status
// and code documented on 00 §9.2 line 336 is a row here, so a code that gains no row fails the
// coverage test below and can never reach a page unclassified. No TestBed: the mapper is pure.

import type { ErrorEnvelope, ErrorEnvelopeDetail } from '@evidgate/sdk';

import { ApiRequestError, isApiRequestError, type ApiError, type ApiErrorKind } from './api-error';
import { mapApiError, mapNetworkError, parseRetryAfter } from './error-mapper';

/** A 00 §9.2 envelope with the defaults a server response would carry. */
function envelope(
  code: string,
  options: {
    message?: string;
    retryable?: boolean;
    requestId?: string;
    details?: ErrorEnvelopeDetail[];
  } = {},
): ErrorEnvelope {
  const {
    message = 'The server refused the request.',
    retryable = false,
    requestId = 'req-4f1c9a',
    details,
  } = options;
  return {
    error: {
      code,
      message,
      request_id: requestId,
      retryable,
      ...(details === undefined ? {} : { details }),
    },
  };
}

interface EnvelopeCase {
  readonly status: number;
  readonly code: string;
  readonly kind: ApiErrorKind;
  /** `Retry-After` header to send; the mapping must echo it as whole seconds. */
  readonly retryAfter?: number;
}

/** 00 §9.2 line 336, one row per documented status/code pair. */
const ENVELOPE_CASES: readonly EnvelopeCase[] = [
  // 401 (also 02.4.1 token codes, all 401).
  { status: 401, code: 'unauthenticated', kind: 'unauthenticated' },
  // 403: only `csrf_failed` has its own kind; every other 403 is `forbidden`.
  { status: 403, code: 'forbidden', kind: 'forbidden' },
  { status: 403, code: 'csrf_failed', kind: 'csrf_failed' },
  { status: 403, code: 'project_scope_mismatch', kind: 'forbidden' },
  { status: 403, code: 'producer_slot_taken', kind: 'forbidden' },
  { status: 403, code: 'waiver_self_approval', kind: 'forbidden' },
  { status: 403, code: 'project_required', kind: 'forbidden' },
  // 404, including other-tenant and other-project ids.
  { status: 404, code: 'not_found', kind: 'not_found' },
  { status: 404, code: 'artifact_not_found', kind: 'not_found' },
  // 409: only `idempotency_conflict` has its own kind; every other 409 is `conflict`.
  { status: 409, code: 'invalid_transition', kind: 'conflict' },
  { status: 409, code: 'idempotency_conflict', kind: 'idempotency_conflict' },
  { status: 409, code: 'conflict', kind: 'conflict' },
  { status: 409, code: 'duplicate_different_payload', kind: 'conflict' },
  { status: 409, code: 'run_closed', kind: 'conflict' },
  { status: 409, code: 'slug_taken', kind: 'conflict' },
  { status: 409, code: 'org_suspended', kind: 'conflict' },
  { status: 409, code: 'incident_not_triaged', kind: 'conflict' },
  { status: 409, code: 'incident_closed', kind: 'conflict' },
  { status: 409, code: 'capability_conflict', kind: 'conflict' },
  { status: 409, code: 'waiver_state_conflict', kind: 'conflict' },
  { status: 409, code: 'quarantine_conflict', kind: 'conflict' },
  { status: 409, code: 'project_deleting', kind: 'conflict' },
  { status: 409, code: 'artifact_state_conflict', kind: 'conflict' },
  { status: 409, code: 'authoring_not_empty', kind: 'conflict' },
  { status: 409, code: 'case_prefix_taken', kind: 'conflict' },
  // 412: the current revision id arrives in `details`.
  { status: 412, code: 'precondition_failed', kind: 'precondition_failed' },
  // 413: every limit breach refused before any write.
  { status: 413, code: 'too_large', kind: 'payload_too_large' },
  { status: 413, code: 'batch_too_large', kind: 'payload_too_large' },
  { status: 413, code: 'payload_too_large', kind: 'payload_too_large' },
  // 422: one `validation` kind; the server code names the offending field.
  { status: 422, code: 'artifact_invalid', kind: 'validation' },
  { status: 422, code: 'critical_case_quarantined', kind: 'validation' },
  { status: 422, code: 'manifest_invalid', kind: 'validation' },
  { status: 422, code: 'event_invalid', kind: 'validation' },
  { status: 422, code: 'sequence_reused', kind: 'validation' },
  { status: 422, code: 'unknown_producer', kind: 'validation' },
  { status: 422, code: 'request_invalid', kind: 'validation' },
  { status: 422, code: 'assignee_invalid', kind: 'validation' },
  { status: 422, code: 'locale_invalid', kind: 'validation' },
  { status: 422, code: 'field_not_patchable', kind: 'validation' },
  { status: 422, code: 'policy_unknown_field', kind: 'validation' },
  { status: 422, code: 'policy_invalid', kind: 'validation' },
  { status: 422, code: 'capability_invalid', kind: 'validation' },
  { status: 422, code: 'unsupported_protocol_major', kind: 'validation' },
  { status: 422, code: 'critical_case_not_quarantinable', kind: 'validation' },
  { status: 422, code: 'object_key_not_allowed', kind: 'validation' },
  { status: 422, code: 'kind_unknown', kind: 'validation' },
  { status: 422, code: 'checksum_invalid', kind: 'validation' },
  { status: 422, code: 'size_out_of_range', kind: 'validation' },
  { status: 422, code: 'release_unresolvable', kind: 'validation' },
  { status: 422, code: 'execution_key_conflict', kind: 'validation' },
  { status: 422, code: 'replay_grant_invalid', kind: 'validation' },
  { status: 422, code: 'replay_release_not_allowed', kind: 'validation' },
  { status: 422, code: 'unknown_execution_key', kind: 'validation' },
  { status: 422, code: 'media_type_not_allowed', kind: 'validation' },
  { status: 422, code: 'kind_disabled', kind: 'validation' },
  { status: 422, code: 'authoring_parent_invalid', kind: 'validation' },
  // 429: with `Retry-After`, for a burst and for a tenant/project quota alike.
  { status: 429, code: 'rate_limited', kind: 'rate_limited', retryAfter: 120 },
  { status: 429, code: 'quota_exceeded', kind: 'rate_limited', retryAfter: 3600 },
  // 503: with `Retry-After`.
  { status: 503, code: 'unavailable', kind: 'unavailable', retryAfter: 30 },
];

/** 00 §9.2 line 336, written out independently of the table so a dropped row fails a test. */
const DOCUMENTED: readonly string[] = [
  '401 unauthenticated',
  '403 forbidden',
  '403 csrf_failed',
  '403 project_scope_mismatch',
  '403 producer_slot_taken',
  '403 waiver_self_approval',
  '403 project_required',
  '404 not_found',
  '404 artifact_not_found',
  '409 invalid_transition',
  '409 idempotency_conflict',
  '409 conflict',
  '409 duplicate_different_payload',
  '409 run_closed',
  '409 slug_taken',
  '409 org_suspended',
  '409 incident_not_triaged',
  '409 incident_closed',
  '409 capability_conflict',
  '409 waiver_state_conflict',
  '409 quarantine_conflict',
  '409 project_deleting',
  '409 artifact_state_conflict',
  '409 authoring_not_empty',
  '409 case_prefix_taken',
  '412 precondition_failed',
  '413 too_large',
  '413 batch_too_large',
  '413 payload_too_large',
  '422 artifact_invalid',
  '422 critical_case_quarantined',
  '422 manifest_invalid',
  '422 event_invalid',
  '422 sequence_reused',
  '422 unknown_producer',
  '422 request_invalid',
  '422 assignee_invalid',
  '422 locale_invalid',
  '422 field_not_patchable',
  '422 policy_unknown_field',
  '422 policy_invalid',
  '422 capability_invalid',
  '422 unsupported_protocol_major',
  '422 critical_case_not_quarantinable',
  '422 object_key_not_allowed',
  '422 kind_unknown',
  '422 checksum_invalid',
  '422 size_out_of_range',
  '422 release_unresolvable',
  '422 execution_key_conflict',
  '422 replay_grant_invalid',
  '422 replay_release_not_allowed',
  '422 unknown_execution_key',
  '422 media_type_not_allowed',
  '422 kind_disabled',
  '422 authoring_parent_invalid',
  '429 rate_limited',
  '429 quota_exceeded',
  '503 unavailable',
];

describe('mapApiError — 00 §9.2 status and code table', () => {
  it.each(ENVELOPE_CASES)('$status $code → $kind', ({ status, code, kind, retryAfter }) => {
    const headers =
      retryAfter === undefined ? null : new Headers({ 'Retry-After': `${retryAfter}` });
    const mapped = mapApiError({ status, body: envelope(code), headers });

    expect(mapped.kind).toBe(kind);
    expect(mapped.status).toBe(status);
    expect(mapped.code).toBe(code);
    expect(mapped.retryAfterSeconds).toBe(retryAfter ?? null);
  });

  it('covers every status and code documented on 00 §9.2', () => {
    const rows = ENVELOPE_CASES.map((row) => `${row.status} ${row.code}`);

    expect(DOCUMENTED.filter((pair) => !rows.includes(pair))).toEqual([]);
    expect(rows.filter((pair) => !DOCUMENTED.includes(pair))).toEqual([]);
    expect(new Set(rows).size).toBe(rows.length);
  });

  it('assigns exactly one kind per documented status', () => {
    const kindsByStatus = new Map<number, Set<ApiErrorKind>>();
    for (const row of ENVELOPE_CASES) {
      const kinds = kindsByStatus.get(row.status) ?? new Set<ApiErrorKind>();
      kinds.add(mapApiError({ status: row.status, body: envelope(row.code) }).kind);
      kindsByStatus.set(row.status, kinds);
    }
    const twoKinds = [...kindsByStatus.entries()].filter(([, kinds]) => kinds.size > 1);
    // 403 and 409 are the only statuses the spec splits by code.
    expect(twoKinds.map(([status]) => status).sort()).toEqual([403, 409]);
  });
});

describe('mapApiError — envelope fields and details', () => {
  it('carries the 412 details (the current revision id) unchanged', () => {
    const details: ErrorEnvelopeDetail[] = [{ path: '/revision_id', reason: 'stale' }];
    const mapped = mapApiError({
      status: 412,
      body: envelope('precondition_failed', { details }),
    });

    expect(mapped.kind).toBe('precondition_failed');
    expect(mapped.details).toBe(details);
    expect(mapped.details[0]).toEqual({ path: '/revision_id', reason: 'stale' });
  });

  it('keeps code, message, request id, retryable and details from the envelope', () => {
    const details: ErrorEnvelopeDetail[] = [{ index: 3, path: '/events/3/sequence', reason: 'x' }];
    const mapped = mapApiError({
      status: 422,
      body: envelope('manifest_invalid', {
        message: 'Manifest is invalid.',
        requestId: 'req-77',
        retryable: true,
        details,
      }),
    });

    expect(mapped.code).toBe('manifest_invalid');
    expect(mapped.message).toBe('Manifest is invalid.');
    expect(mapped.requestId).toBe('req-77');
    expect(mapped.retryable).toBe(true);
    expect(mapped.details).toBe(details);
  });

  it('uses an empty details list when the envelope omits it', () => {
    const mapped = mapApiError({ status: 409, body: envelope('slug_taken') });

    expect(mapped.details).toEqual([]);
    expect(mapped.requestId).toBe('req-4f1c9a');
  });
});

describe('mapApiError — non-envelope failures', () => {
  it('maps an HTML 502 to unexpected / envelope_missing and keeps it retryable', () => {
    const mapped = mapApiError({
      status: 502,
      body: '<html><body><h1>502 Bad Gateway</h1></body></html>',
    });

    expect(mapped.kind).toBe('unexpected');
    expect(mapped.code).toBe('envelope_missing');
    expect(mapped.status).toBe(502);
    expect(mapped.retryable).toBe(true);
    expect(mapped.requestId).toBeNull();
    expect(mapped.details).toEqual([]);
    expect(mapped.retryAfterSeconds).toBeNull();
  });

  it('maps a 500 with a null body to unexpected / envelope_missing, not retryable', () => {
    const mapped = mapApiError({ status: 500, body: null });

    expect(mapped.kind).toBe('unexpected');
    expect(mapped.code).toBe('envelope_missing');
    expect(mapped.retryable).toBe(false);
    expect(mapped.requestId).toBeNull();
  });

  it('honours Retry-After on an envelope-less 503 and keeps it retryable', () => {
    const mapped = mapApiError({
      status: 503,
      body: 'Service Unavailable',
      headers: new Headers({ 'Retry-After': '45' }),
    });

    expect(mapped.kind).toBe('unexpected');
    expect(mapped.retryable).toBe(true);
    expect(mapped.retryAfterSeconds).toBe(45);
  });

  it('rejects a body whose details are not the 00 §9.2 shape', () => {
    const mapped = mapApiError({
      status: 422,
      body: {
        error: {
          code: 'manifest_invalid',
          message: 'x',
          request_id: 'req-1',
          retryable: false,
          details: [{ path: '/events' }],
        },
      },
    });

    expect(mapped.kind).toBe('unexpected');
    expect(mapped.code).toBe('envelope_missing');
  });

  it('accepts a headers stand-in that looks names up case-sensitively', () => {
    const mapped = mapApiError({
      status: 429,
      body: envelope('rate_limited'),
      headers: { get: (name: string) => (name === 'retry-after' ? '7' : null) },
    });

    expect(mapped.kind).toBe('rate_limited');
    expect(mapped.retryAfterSeconds).toBe(7);
  });
});

describe('mapApiError — a success status is a programming error', () => {
  it.each([200, 201, 204])('%i is rejected as unexpected_status', (status) => {
    const mapped = mapApiError({ status, body: envelope('not_found') });

    expect(mapped.kind).toBe('unexpected');
    expect(mapped.code).toBe('unexpected_status');
    expect(mapped.status).toBe(status);
    expect(mapped.retryable).toBe(false);
    expect(mapped.requestId).toBeNull();
  });
});

describe('mapApiError — 403 and 404 keep their meaning', () => {
  it.each([
    'forbidden',
    'csrf_failed',
    'project_scope_mismatch',
    'producer_slot_taken',
    'waiver_self_approval',
    'project_required',
    'some_new_code',
  ])('403 %s never maps to a success kind', (code) => {
    const mapped = mapApiError({ status: 403, body: envelope(code) });

    expect(['forbidden', 'csrf_failed']).toContain(mapped.kind);
  });

  it('maps a 404 without echoing request data', () => {
    const mapped = mapApiError({
      status: 404,
      body: envelope('not_found', { message: 'Not found.' }),
    });

    expect(mapped.kind).toBe('not_found');
    expect(mapped.code).toBe('not_found');
    expect(mapped.message).toBe('Not found.');
    expect(mapped.message).not.toContain('/v1/');
  });
});

describe('mapNetworkError', () => {
  it('maps a failed fetch to network, status 0, retryable', () => {
    const mapped = mapNetworkError(new TypeError('Failed to fetch'));

    expect(mapped.kind).toBe('network');
    expect(mapped.status).toBe(0);
    expect(mapped.code).toBe('network');
    expect(mapped.retryable).toBe(true);
    expect(mapped.requestId).toBeNull();
    expect(mapped.retryAfterSeconds).toBeNull();
    expect(mapped.details).toEqual([]);
  });

  it('names a cancellation without quoting browser text', () => {
    const abort = new DOMException('The operation was aborted.', 'AbortError');
    const mapped = mapNetworkError(abort);

    expect(mapped.kind).toBe('network');
    expect(mapped.status).toBe(0);
    expect(mapped.message).not.toContain('The operation was aborted.');
  });

  it('maps a status below 1 given to mapApiError the same way', () => {
    const mapped = mapApiError({ status: 0, body: null });

    expect(mapped.kind).toBe('network');
    expect(mapped.status).toBe(0);
    expect(mapped.retryable).toBe(true);
  });
});

describe('parseRetryAfter', () => {
  it.each([
    { header: '120', expected: 120 },
    { header: '0', expected: 0 },
    { header: ' 45 ', expected: 45 },
    // A bare four-digit value is delta-seconds, never a year (RFC 9110 `1*DIGIT`).
    { header: '2026', expected: 2026 },
  ])('reads delta-seconds "$header" as $expected seconds', ({ header, expected }) => {
    expect(parseRetryAfter(header)).toBe(expected);
  });

  it('reads an HTTP-date against the given clock', () => {
    const now = Date.parse('2026-10-24T10:00:00Z');

    expect(parseRetryAfter('Sat, 24 Oct 2026 10:02:00 GMT', now)).toBe(120);
  });

  it('reads the obsolete RFC 850 form the same way', () => {
    const now = Date.parse('2026-10-24T10:00:00Z');

    expect(parseRetryAfter('Saturday, 24-Oct-26 10:02:00 GMT', now)).toBe(120);
  });

  it('clamps an HTTP-date already in the past to 0', () => {
    const now = Date.parse('2026-10-24T10:05:00Z');

    expect(parseRetryAfter('Sat, 24 Oct 2026 10:02:00 GMT', now)).toBe(0);
  });

  it('rounds a sub-second remainder up so the client never retries early', () => {
    const now = Date.parse('2026-10-24T10:01:59.500Z');

    expect(parseRetryAfter('Sat, 24 Oct 2026 10:02:00 GMT', now)).toBe(1);
  });

  it.each([
    '',
    '   ',
    'soon',
    '-5',
    '1.5',
    '120abc',
    'Sat, 24 Oct 2026',
    '2026-10-24',
    '2026-10-24T10:00:00Z',
    null,
    undefined,
  ])('rejects the header value %s', (value) => {
    expect(parseRetryAfter(value)).toBeNull();
  });
});

describe('ApiRequestError', () => {
  const error: ApiError = {
    kind: 'forbidden',
    status: 403,
    code: 'forbidden',
    message: 'No.',
    requestId: 'req-1',
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  };

  it('carries the ApiError and its message', () => {
    const thrown = new ApiRequestError(error);

    expect(thrown).toBeInstanceOf(Error);
    expect(thrown.name).toBe('ApiRequestError');
    expect(thrown.message).toBe('No.');
    expect(thrown.error).toBe(error);
  });

  it('narrows only an ApiRequestError', () => {
    expect(isApiRequestError(new ApiRequestError(error))).toBe(true);
    expect(isApiRequestError({ error })).toBe(false);
    expect(isApiRequestError(error)).toBe(false);
    expect(isApiRequestError(null)).toBe(false);
  });
});
