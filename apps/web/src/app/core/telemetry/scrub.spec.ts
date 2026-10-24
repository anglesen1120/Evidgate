// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: client error reporting with
// PII/secret scrubbing, ids only): the per-attribute schema filter, table-driven.
//
// Every key of `ALLOWED_ATTRIBUTE_KEYS` has a row, and most keys also get a refusal: a method is
// a token, a status is a range, a route is a *template* (its ids become `:id`), an enum is one of
// its values, a vital is a finite number. Keeping a clean status line is worth nothing if the
// same filter keeps a concrete id, so the rows that feed a value the key must not carry — a route
// holding `/projects/<uuid>/runs`, a route holding a dotted identifier, a locale holding a
// sentence, an `error.message` at all (D-S69-Q2) — are the point of the table.
//
// There is no row for bare text because no attribute carries text: a span or event name is
// admitted only as one of the fixed constants of `telemetry-names.ts`, so content cannot reach a
// name, and `error.message` is not an attribute. Row samples are assembled at runtime (`join`,
// `repeat`) so no credential or e-mail literal sits in the repository.
import type { AttributeValue, Attributes } from '@opentelemetry/api';

import { scrubAttributes } from './scrub';

/** Row samples, assembled at runtime so no credential or e-mail literal sits in the repo. */
const EMAIL = ['julia.moreau', 'example.test'].join('@');
const RUN_ID = ['3f2b9d54', '0d7e', '4c1a', '9a2e', '6b0f5c7d81c4'].join('-');
const CREDENTIAL = 'abcdef1234567890';

/** A cast through the index signature so a row may hold an array or an object, both of which drop. */
function attributesOf(entries: Readonly<Record<string, unknown>>): Attributes {
  return entries as Attributes;
}

interface AttributeRow {
  readonly rule: string;
  readonly key: string;
  readonly value: unknown;
  /** `undefined` means the key is dropped. */
  readonly expected: AttributeValue | undefined;
}

const ATTRIBUTE_ROWS: AttributeRow[] = [
  // The schema gate: an unlisted key never survives, whatever it holds.
  {
    rule: 'drops a non-allowlisted key holding a string',
    key: 'user.email',
    value: EMAIL,
    expected: undefined,
  },
  {
    rule: 'drops a non-allowlisted key holding a number',
    key: 'http.request.body_size',
    value: 512,
    expected: undefined,
  },
  // Values that are not a finite primitive.
  {
    rule: 'drops an array value',
    key: 'http.request.method',
    value: ['GET'],
    expected: undefined,
  },
  {
    rule: 'drops an object value',
    key: 'evidgate.api.retryable',
    value: { retryable: true },
    expected: undefined,
  },
  {
    rule: 'drops an undefined value',
    key: 'http.request.method',
    value: undefined,
    expected: undefined,
  },
  { rule: 'drops NaN', key: 'web_vital.value', value: Number.NaN, expected: undefined },
  {
    rule: 'drops Infinity',
    key: 'web_vital.value',
    value: Number.POSITIVE_INFINITY,
    expected: undefined,
  },
  // http.request.method
  {
    rule: 'keeps an uppercase method',
    key: 'http.request.method',
    value: 'PATCH',
    expected: 'PATCH',
  },
  {
    rule: 'drops a lowercase method',
    key: 'http.request.method',
    value: 'get',
    expected: undefined,
  },
  // http.response.status_code
  {
    rule: 'keeps a status code',
    key: 'http.response.status_code',
    value: 204,
    expected: 204,
  },
  {
    rule: 'drops a status code below 100',
    key: 'http.response.status_code',
    value: 99,
    expected: undefined,
  },
  {
    rule: 'drops a status code above 599',
    key: 'http.response.status_code',
    value: 600,
    expected: undefined,
  },
  {
    rule: 'drops a fractional status code',
    key: 'http.response.status_code',
    value: 200.5,
    expected: undefined,
  },
  // http.route and evidgate.route: templates only.
  {
    rule: 'keeps an API route template',
    key: 'http.route',
    value: '/v1/projects/:id/runs',
    expected: '/v1/projects/:id/runs',
  },
  {
    rule: 'templates a UUID inside a route',
    key: 'http.route',
    value: `/v1/projects/${RUN_ID}/runs`,
    expected: '/v1/projects/:id/runs',
  },
  {
    rule: 'templates a long number inside a route',
    key: 'http.route',
    value: '/v1/runs/1234567/cancel',
    expected: '/v1/runs/:id/cancel',
  },
  {
    rule: 'templates the id of a concrete route and keeps its words',
    key: 'evidgate.route',
    value: '/projects/acme-corp/runs/p-1',
    expected: '/projects/acme-corp/runs/:id',
  },
  {
    rule: 'templates a numeric id in a concrete route',
    key: 'evidgate.route',
    value: '/projects/acme/runs/1234567',
    expected: '/projects/acme/runs/:id',
  },
  {
    rule: 'keeps a version prefix but templates the id after it',
    key: 'evidgate.route',
    value: '/v1/runs/p-1',
    expected: '/v1/runs/:id',
  },
  {
    rule: 'templates a dotted identifier in a route',
    key: 'evidgate.route',
    value: `/projects/${['julia', 'moreau'].join('.')}/runs`,
    expected: '/projects/:id/runs',
  },
  {
    rule: 'templates a dotted identifier in an API route',
    key: 'http.route',
    value: `/v1/projects/${['acme', 'corp'].join('.')}/runs`,
    expected: '/v1/projects/:id/runs',
  },
  {
    rule: 'drops a route carrying a query string',
    key: 'evidgate.route',
    value: '/v1/runs?x=1',
    expected: undefined,
  },
  {
    rule: 'drops a route that does not start with a slash',
    key: 'evidgate.route',
    value: 'v1/runs',
    expected: undefined,
  },
  {
    rule: 'keeps a UI route template',
    key: 'evidgate.route',
    value: '/projects/:projectId/runs/:runId',
    expected: '/projects/:projectId/runs/:runId',
  },
  { rule: 'keeps the wildcard route', key: 'evidgate.route', value: '/**', expected: '/**' },
  { rule: 'keeps the root route', key: 'evidgate.route', value: '/', expected: '/' },
  // evidgate.outcome
  {
    rule: 'keeps a known outcome',
    key: 'evidgate.outcome',
    value: 'cancelled',
    expected: 'cancelled',
  },
  {
    rule: 'drops an unknown outcome',
    key: 'evidgate.outcome',
    value: 'failed',
    expected: undefined,
  },
  // evidgate.request_id
  {
    rule: 'keeps a UUID request id',
    key: 'evidgate.request_id',
    value: RUN_ID,
    expected: RUN_ID,
  },
  {
    rule: 'keeps an uppercase UUID request id, lowercased',
    key: 'evidgate.request_id',
    value: RUN_ID.toUpperCase(),
    expected: RUN_ID,
  },
  {
    rule: 'drops a non-UUID request id',
    key: 'evidgate.request_id',
    value: 'req-1',
    expected: undefined,
  },
  // evidgate.locale
  { rule: 'keeps a supported locale', key: 'evidgate.locale', value: 'vi', expected: 'vi' },
  {
    rule: 'drops an unsupported locale',
    key: 'evidgate.locale',
    value: 'fr',
    expected: undefined,
  },
  // evidgate.api.error_code / error_kind / retryable
  {
    rule: 'keeps an error code',
    key: 'evidgate.api.error_code',
    value: 'csrf_failed',
    expected: 'csrf_failed',
  },
  {
    rule: 'drops an uppercase error code',
    key: 'evidgate.api.error_code',
    value: 'CSRF_FAILED',
    expected: undefined,
  },
  {
    rule: 'keeps an error kind',
    key: 'evidgate.api.error_kind',
    value: 'validation',
    expected: 'validation',
  },
  {
    rule: 'drops an error kind with an uppercase letter',
    key: 'evidgate.api.error_kind',
    value: 'Validation',
    expected: undefined,
  },
  {
    rule: 'keeps a boolean retryable',
    key: 'evidgate.api.retryable',
    value: false,
    expected: false,
  },
  {
    rule: 'drops a string retryable',
    key: 'evidgate.api.retryable',
    value: 'true',
    expected: undefined,
  },
  // evidgate.error.source
  {
    rule: 'keeps a known error source',
    key: 'evidgate.error.source',
    value: 'unhandledrejection',
    expected: 'unhandledrejection',
  },
  {
    rule: 'drops an unknown error source',
    key: 'evidgate.error.source',
    value: 'console',
    expected: undefined,
  },
  // error.type; error.message is deliberately not an attribute (D-S69-Q2).
  { rule: 'keeps an error type', key: 'error.type', value: 'TypeError', expected: 'TypeError' },
  { rule: 'drops a numeric error type', key: 'error.type', value: 42, expected: undefined },
  {
    rule: 'keeps a dotted error type',
    key: 'error.type',
    value: 'ZoneAwarePromise.Error',
    expected: 'ZoneAwarePromise.Error',
  },
  {
    rule: 'drops an error type holding a space',
    key: 'error.type',
    value: 'Julia Smith',
    expected: undefined,
  },
  {
    rule: 'drops an error type holding an e-mail address',
    key: 'error.type',
    value: ['boom', 'example.test'].join('@'),
    expected: undefined,
  },
  {
    rule: 'drops an error type starting with a digit',
    key: 'error.type',
    value: '9Lives',
    expected: undefined,
  },
  {
    rule: 'drops an error type longer than the cap',
    key: 'error.type',
    value: 'E'.repeat(129),
    expected: undefined,
  },
  {
    rule: 'drops an error message, which is not a telemetry attribute at all (D-S69-Q2)',
    key: 'error.message',
    value: `Say "hi" to ${EMAIL}`,
    expected: undefined,
  },
  // web_vital.*
  { rule: 'keeps the LCP vital', key: 'web_vital.name', value: 'LCP', expected: 'LCP' },
  { rule: 'drops a non-core web vital', key: 'web_vital.name', value: 'FCP', expected: undefined },
  {
    rule: 'keeps a fractional vital value',
    key: 'web_vital.value',
    value: 1234.5678,
    expected: 1234.5678,
  },
  { rule: 'drops a negative vital value', key: 'web_vital.value', value: -1, expected: undefined },
  {
    rule: 'keeps a vital rating',
    key: 'web_vital.rating',
    value: 'needs-improvement',
    expected: 'needs-improvement',
  },
  {
    rule: 'drops an unknown vital rating',
    key: 'web_vital.rating',
    value: 'bad',
    expected: undefined,
  },
  {
    rule: 'keeps a navigation type',
    key: 'web_vital.navigation_type',
    value: 'back-forward',
    expected: 'back-forward',
  },
  {
    rule: 'drops an uppercase navigation type',
    key: 'web_vital.navigation_type',
    value: 'Back',
    expected: undefined,
  },
];

describe('scrubAttributes', () => {
  it.each(ATTRIBUTE_ROWS)('$rule', (row: AttributeRow) => {
    const scrubbed = scrubAttributes(attributesOf({ [row.key]: row.value }));

    if (row.expected === undefined) {
      expect(scrubbed).not.toHaveProperty(row.key);
    } else {
      expect(scrubbed[row.key]).toBe(row.expected);
    }
  });

  it('keeps every allowlisted key of one span and drops the rest, in one pass', () => {
    const input = attributesOf({
      'http.request.method': 'GET',
      'evidgate.route': '/projects/:projectId/runs',
      'evidgate.locale': 'en',
      'evidgate.outcome': 'error',
      'evidgate.request_id': RUN_ID,
      'error.message': `boom for ${EMAIL}`,
      'http.url': `https://acme.example.test/v1/runs?token=${CREDENTIAL}`,
      'user.email': EMAIL,
    });

    expect(scrubAttributes(input)).toEqual({
      'http.request.method': 'GET',
      'evidgate.route': '/projects/:projectId/runs',
      'evidgate.locale': 'en',
      'evidgate.outcome': 'error',
      'evidgate.request_id': RUN_ID,
    });
  });

  it('never mutates the attributes it is given', () => {
    const input = attributesOf({
      'error.type': 'TypeError',
      'http.route': `/v1/projects/${RUN_ID}/runs`,
      'user.email': EMAIL,
    });
    const before = structuredClone(input);

    scrubAttributes(input);

    expect(input).toEqual(before);
  });

  it('returns an empty record for an empty input', () => {
    expect(scrubAttributes({})).toEqual({});
  });
});
