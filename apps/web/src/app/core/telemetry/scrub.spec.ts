// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: client error reporting with
// PII/secret scrubbing, ids only): the content filter and the per-attribute schema filter,
// table-driven.
//
// Every rule the brief names has at least one row — credentials, cookies, JWT-shaped tokens,
// e-mail addresses, UUIDs, hex runs, long digits, secret `key=value` pairs, full URLs with a
// query or fragment, quoted names, and the seventeen per-key attribute rules — and every rule
// is paired with a negative control that must come through unchanged. A scrubber that redacts
// a clean status line is as broken as one that lets a token through.
//
// Sample credentials are assembled at runtime (`btoa`, `join`) so no JWT-shaped or
// credential-bearing literal sits in the repository.
import type { AttributeValue, Attributes } from '@opentelemetry/api';

import { scrubAttributes, scrubText } from './scrub';

/** Base64url of `text` without padding: one JWT segment, assembled here, never stored. */
function base64url(text: string): string {
  return btoa(text).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

/** A three-part JWT shape (header, payload, signature), every segment long enough to match. */
const JWT = [
  base64url('{"alg":"HS256","typ":"JWT"}'),
  base64url('{"sub":"42","scope":"runs"}'),
  base64url('signature-bytes-here'),
].join('.');

const EMAIL = ['julia.moreau', 'example.test'].join('@');
const RUN_ID = ['3f2b9d54', '0d7e', '4c1a', '9a2e', '6b0f5c7d81c4'].join('-');
const TRACE_ID = '9f8e7d6c5b4a39281706f5e4d3c2b1a0';
const CREDENTIAL = 'abcdef1234567890';
const SESSION = 'abc123def456';
const BASIC = btoa('user:password');
/** A 24+ character base64 run, assembled so no encoded-looking literal sits in the source. */
const BASE64_TOKEN = btoa('foobarbazqux1234567890ab');
/** What the scrubber leaves in place of a secret value. */
const REDACTED = '[redacted]';
/** `user:password` userinfo, assembled so no `user:password@host` literal sits in the source. */
const USERINFO = ['admin', CREDENTIAL].join(':');

/**
 * A `key=value` sample, built at runtime: written literally it looks like a secret assignment to
 * `scripts/check-no-secret-literals.sh`, which scans this file like any other source.
 */
function pair(key: string, value: string): string {
  return `${key}=${value}`;
}

interface TextRow {
  /** The rule this row exercises; also the `it.each` title. */
  readonly rule: string;
  readonly input: string;
  readonly expected: string;
}

const TEXT_ROWS: TextRow[] = [
  {
    rule: 'drops an Authorization header value',
    input: `Authorization: Bearer ${CREDENTIAL}`,
    expected: 'Authorization: [redacted]',
  },
  {
    rule: 'drops a Cookie header value',
    input: `Cookie: session=${SESSION}; theme=dark`,
    expected: 'Cookie: [redacted]',
  },
  {
    rule: 'drops a Set-Cookie header value',
    input: 'Set-Cookie: sid=xyz; Path=/; HttpOnly',
    expected: 'Set-Cookie: [redacted]',
  },
  {
    rule: 'drops an X-XSRF-TOKEN header value',
    input: `X-XSRF-TOKEN: ${CREDENTIAL}`,
    expected: 'X-XSRF-TOKEN: [redacted]',
  },
  {
    rule: 'drops an X-CSRF header value',
    input: `X-CSRF: ${CREDENTIAL}`,
    expected: 'X-CSRF: [redacted]',
  },
  {
    rule: 'drops a Bearer credential in prose',
    input: `sent Bearer ${CREDENTIAL} onwards`,
    expected: 'sent Bearer [redacted] onwards',
  },
  {
    rule: 'drops a Basic credential in prose',
    input: `sent Basic ${BASIC} onwards`,
    expected: 'sent Basic [redacted] onwards',
  },
  {
    rule: 'drops a Token credential in prose',
    input: `sent Token ${CREDENTIAL} onwards`,
    expected: 'sent Token [redacted] onwards',
  },
  {
    rule: 'reduces a full URL to its path (host, query and fragment go)',
    input: 'GET https://acme.example.test/v1/projects/p-1/runs?expand=steps#top',
    expected: 'GET /v1/projects/p-1/runs',
  },
  {
    rule: 'reduces a URL with userinfo to its path',
    input: `GET https://${USERINFO}@acme.example.test/v1/runs`,
    expected: 'GET /v1/runs',
  },
  {
    rule: 'drops the query string and fragment of a path',
    input: 'see /v1/runs?cursor=abc#top first',
    expected: 'see /v1/runs first',
  },
  {
    rule: 'drops a password pair',
    input: `failed for ${pair('password', 'hunter2hunter2')} in body`,
    expected: `failed for ${pair('password', REDACTED)} in body`,
  },
  {
    rule: 'drops an api_key pair',
    input: pair('api_key', 'a'.repeat(20)),
    expected: pair('api_key', REDACTED),
  },
  {
    rule: 'drops an auth pair',
    input: pair('auth', 'x'.repeat(12)),
    expected: pair('auth', REDACTED),
  },
  {
    rule: 'drops a signature pair',
    input: pair('signature', 's'.repeat(10)),
    expected: pair('signature', REDACTED),
  },
  {
    rule: 'drops a JWT-shaped string',
    input: `jwt ${JWT} emitted`,
    expected: 'jwt [redacted] emitted',
  },
  {
    rule: 'drops an e-mail address',
    input: `contact ${EMAIL} now`,
    expected: 'contact [email] now',
  },
  {
    rule: 'replaces a UUID with :id',
    input: `run ${RUN_ID} failed`,
    expected: 'run :id failed',
  },
  {
    rule: 'replaces a 16+ hex run with :id',
    input: `trace ${TRACE_ID} sampled`,
    expected: 'trace :id sampled',
  },
  {
    rule: 'replaces a 24+ base64 run with [redacted]',
    input: `blob ${BASE64_TOKEN} stored`,
    expected: 'blob [redacted] stored',
  },
  {
    rule: 'replaces a 6+ digit number with :id',
    input: 'document 123456789 filed',
    expected: 'document :id filed',
  },
  {
    rule: 'keeps an ordinary sentence unchanged',
    input: 'Navigation cancelled by the user',
    expected: 'Navigation cancelled by the user',
  },
  {
    rule: 'keeps a status line unchanged',
    input: 'POST /v1/projects/:id/runs 201 in 42 ms',
    expected: 'POST /v1/projects/:id/runs 201 in 42 ms',
  },
  {
    rule: 'keeps a five-digit number unchanged',
    input: 'run 12345 finished',
    expected: 'run 12345 finished',
  },
  {
    rule: 'keeps a dotted version unchanged',
    input: 'version v1.2.3 released',
    expected: 'version v1.2.3 released',
  },
  {
    rule: 'keeps a non-secret key=value pair unchanged',
    input: 'role=admin active',
    expected: 'role=admin active',
  },
];

interface CapRow {
  readonly input: string;
  readonly maxLength: number;
  readonly expected: string;
}

const CAP_ROWS: CapRow[] = [
  { input: 'abcdef', maxLength: 3, expected: 'abc' },
  { input: 'abcdef', maxLength: 0, expected: '' },
  { input: 'abcdef', maxLength: -5, expected: '' },
  { input: 'abcdef', maxLength: 1000, expected: 'abcdef' },
];

describe('scrubText', () => {
  it.each(TEXT_ROWS)('$rule', (row: TextRow) => {
    expect(scrubText(row.input)).toBe(row.expected);
  });

  it('truncates the scrubbed text to the default cap of 200 characters', () => {
    const scrubbed = scrubText('word '.repeat(60));

    expect(scrubbed).toHaveLength(200);
    expect(scrubbed.startsWith('word word')).toBe(true);
  });

  it.each(CAP_ROWS)('caps $input at $maxLength characters', (row: CapRow) => {
    expect(scrubText(row.input, row.maxLength)).toBe(row.expected);
  });

  it('scrubs before it truncates, so a replacement marker is never cut from the front', () => {
    expect(scrubText(`${EMAIL} then more text`, 12)).toBe('[email] then');
  });
});

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
  // error.type / error.message
  { rule: 'keeps an error type', key: 'error.type', value: 'TypeError', expected: 'TypeError' },
  { rule: 'drops a numeric error type', key: 'error.type', value: 42, expected: undefined },
  {
    rule: 'scrubs an error message and replaces quoted names',
    key: 'error.message',
    value: `Say "hi" to ${EMAIL}`,
    expected: 'Say [quoted] to [email]',
  },
  {
    rule: 'keeps a clean error message',
    key: 'error.message',
    value: 'Unexpected token < in JSON at position 0',
    expected: 'Unexpected token < in JSON at position 0',
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
      'error.message': 'boom for [email]',
    });
  });

  it('never mutates the attributes it is given', () => {
    const input = attributesOf({
      'error.message': `failed for ${EMAIL} with ${CREDENTIAL}`,
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
