// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1; 09.2.1-T1 hand-over, S71, #159; T38): the `returnUrl` validator.
// Every case is one shape a query parameter can arrive in, and the accepted list is as important as
// the refused one: a validator that refuses a legitimate deep link sends the caller home after
// signing in, which is the same defect as allowing an open redirect, only quieter.
//
// The refused table is the attack surface the reviewer must be able to read at a glance:
//   //host, /\host, any scheme, data:, javascript:, encoded and double-encoded forms of both, control
//   characters, malformed escapes, over-long values, and the two sign-in paths themselves (a loop).

import { MAX_RETURN_URL_LENGTH, validateReturnUrl } from './return-url';

/** What the workspace root is; every refusal answers it. */
const HOME = '/';

/** Values that are a same-origin path this app can navigate to: they come back unchanged. */
const ACCEPTED: readonly string[] = [
  '/',
  '/orgs/018f0000-0000-7000-8000-000000000001',
  '/orgs/o-1/projects/p-1/overview',
  '/ok/path?x=1#h',
  '/runs?status=failed&page=2',
  '/runs#top',
  // A colon inside a path segment is legal; it is not a scheme (the value starts with `/`).
  '/orgs/o-1/a:b',
  // A matrix parameter on a product path is harmless: only the sign-in routes loop.
  '/orgs/o-1;v=2/projects/p-1',
  // A percent-encoded character that stays inside one segment, in its raw spelling.
  '/runs/a%20b',
  '/orgs/o-1/projects/p-1?tab=evidence%20list',
  // A trailing slash is the same route to the router; it is not refused.
  '/orgs/o-1/',
  // Exactly the documented ceiling is still a path this app accepts.
  `/${'a'.repeat(MAX_RETURN_URL_LENGTH - 1)}`,
];

/** Values no navigation may receive: they all answer `/`. */
const REFUSED: readonly string[] = [
  // Not a path at all.
  'foo',
  'foo/bar',
  '',
  // A scheme. A scheme can only live before the first `/`, so requiring one leading `/` removes it.
  'https://evil.example',
  'http://evil.example/steal',
  'javascript:alert(document.title)',
  'JavaScript:alert(1)',
  'data:text/html,<script>alert(1)</script>',
  'mailto:someone@evil.example',
  // Protocol-relative and backslash forms, raw and encoded, one to three layers deep.
  '//evil.example',
  '///evil.example',
  '/\\evil.example',
  '/\\\\evil.example',
  '/%2F%2Fevil.example',
  '/%2f%2fevil.example',
  '/%252F%252Fevil.example',
  '/%25252Fevil.example',
  '/%5Cevil.example',
  '/%255Cevil.example',
  // An encoded leading slash tries to become a second one.
  '/%2Fevil.example',
  // Control characters, raw and encoded: a CR/LF or NUL can split a header or a URL.
  '/ok\nx',
  '/ok\r\nx',
  '/ok\u0000x',
  '/ok\u0007x',
  '/ok\u007fx',
  '/ok%0Ax',
  '/ok%00x',
  // The C1 block and the JavaScript line separators are control characters too.
  '/ok\u0085x',
  '/ok\u009f x',
  '/ok\u2028x',
  '/ok\u2029x',
  '/ok%C2%85x',
  // Malformed percent-encoding is refused rather than passed through half-decoded.
  '/%zz',
  '/ok%',
  '/ok%2',
  // Over the ceiling by one character.
  `/${'a'.repeat(MAX_RETURN_URL_LENGTH)}`,
  // The sign-in flow itself, in every spelling: sign-in would loop.
  '/login',
  '/login/',
  '/login?returnUrl=%2Forgs%2Fo-1',
  '/login#top',
  '/%6Cogin',
  // Matrix parameters are stripped before matching, so these are still the sign-in routes.
  '/login;x=1',
  '/auth/callback;ticket=1',
  '/auth/callback;x=1/;y=2',
  '/auth/callback',
  '/auth/callback?returnUrl=%2Forgs%2Fo-1',
  '/auth/callback/',
];

describe('validateReturnUrl (09.1.1-T5.1)', () => {
  it.each([...ACCEPTED])('accepts %j unchanged', (value) => {
    expect(validateReturnUrl(value)).toBe(value);
  });

  it.each([...REFUSED])('refuses %j and answers the workspace root', (value) => {
    expect(validateReturnUrl(value)).toBe(HOME);
  });

  it('answers the workspace root for a missing parameter', () => {
    expect(validateReturnUrl(null)).toBe(HOME);
    expect(validateReturnUrl(undefined)).toBe(HOME);
  });

  it('answers the workspace root for a value that is not a string', () => {
    // A query parameter is always a string, but the function is also called with a route snapshot's
    // `get()`, and a hostile caller can hand it anything.
    expect(validateReturnUrl(42 as unknown as string)).toBe(HOME);
  });

  it('inspects at most four encoded layers and refuses a fifth', () => {
    // One encoded layer is inspected and accepted when decoding keeps a single-slash path: `%2D` is
    // a hyphen, so the target is the same path in a different spelling.
    expect(validateReturnUrl('/orgs/o%2D1')).toBe('/orgs/o%2D1');
    // An encoded separator does not make a `//` path safe: `/%2Forgs…` decodes to `//orgs…`, which
    // is protocol-relative, so the first decode already refuses it.
    expect(validateReturnUrl('/%2Forgs%2Fo-1')).toBe(HOME);
    // `%252F` decodes to `%2F`, then to `//evil.example` at the third pass: refused.
    expect(validateReturnUrl('/%25252F%25252Fevil.example')).toBe(HOME);
    // Five layers of `%25` in front of a safe path are still changing after four passes.
    expect(validateReturnUrl('/%2525252520safe')).toBe(HOME);
  });

  it('returns the raw spelling, never the decoded one', () => {
    // Returning the decoded value would turn `%3F` into a query separator and change the route.
    const raw = '/runs/a%3Fb';
    expect(validateReturnUrl(raw)).toBe(raw);
    expect(validateReturnUrl(raw)).not.toContain('?');
  });

  it('is a pure function: the same input always answers the same string', () => {
    for (const value of [...ACCEPTED, ...REFUSED]) {
      expect(validateReturnUrl(value)).toBe(validateReturnUrl(value));
    }
  });
});
