// 09.1.1-T10 (AC7, ADR-0008 rule 4): proofs for apps/web/scripts/csp.mjs and for the repo wiring
// the policy depends on (the src/index.html placeholder and the angular.json build flags). Each
// case asserts an observable property of the produced header or of the files that feed it, so the
// suite fails when the policy silently drifts rather than when the implementation is rewritten in
// an equivalent way. The deploy JSON needs no shape re-check — importing the module already
// refuses a malformed one and the fixed-policy case pins the bytes it produces — but its nonce
// marker is pinned below, because the deployed edge substitutes that exact token from that file.
//
// Repo files are resolved from this test's own location (`new URL('../src/index.html', ...)`), so
// the suite behaves identically whatever the caller's cwd is — the same convention as
// check-bundle-budget.test.mjs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import {
  NONCE_PLACEHOLDER,
  assertConnectSrcAppendable,
  assertStrictPolicy,
  buildCsp,
  buildSecurityHeaders,
  generateNonce,
  injectNonce,
  isValidNonce,
  parseConnectSrc,
} from './csp.mjs';

const VALID_NONCE = `${'A'.repeat(22)}==`;

/** The whole policy, spelled once: order, quoting and separators are all part of the contract. */
const EXPECTED_POLICY =
  `default-src 'none'; script-src 'self'; style-src 'self' 'nonce-${VALID_NONCE}'; ` +
  `connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'self'; ` +
  `form-action 'self'; frame-ancestors 'none'; object-src 'none'; ` +
  `require-trusted-types-for 'script'; trusted-types angular`;

const INDEX_HTML_PATH = new URL('../src/index.html', import.meta.url);
const ANGULAR_JSON_PATH = new URL('../angular.json', import.meta.url);
const POLICY_JSON_PATH = new URL('./app-origin-csp.json', import.meta.url);

function readJson(url) {
  return JSON.parse(readFileSync(url, 'utf8'));
}

/** Assert that `run` throws an Error whose message quotes `entry`. */
function assertThrowsNaming(entry, run) {
  assert.throws(run, (error) => {
    assert.ok(error instanceof Error, 'must throw an Error');
    assert.ok(error.message.includes(entry), `error must name ${entry}, got: ${error.message}`);
    return true;
  });
}

/** Split a header value into directive name -> source list, keeping order. */
function splitPolicy(policy) {
  return new Map(
    policy
      .split(';')
      .map((part) => part.trim())
      .filter((part) => part !== '')
      .map((part) => {
        const [name, ...sources] = part.split(/\s+/);
        return [name, sources];
      }),
  );
}

/** True when any object key in the parsed JSON is named `name`, at any depth. */
function containsKey(node, name) {
  if (Array.isArray(node)) return node.some((child) => containsKey(child, name));
  if (node !== null && typeof node === 'object') {
    return Object.entries(node).some(([key, value]) => key === name || containsKey(value, name));
  }
  return false;
}

test('buildCsp emits the ADR-0008 policy for a fixed nonce, in the deploy JSON order', () => {
  assert.equal(buildCsp({ nonce: VALID_NONCE }), EXPECTED_POLICY);
  assert.deepEqual(buildSecurityHeaders({ nonce: VALID_NONCE }), {
    'content-security-policy': EXPECTED_POLICY,
  });
});

test('the nonce reaches style-src only and leaves script-src at self', () => {
  const policy = buildCsp({ nonce: VALID_NONCE });
  const directives = splitPolicy(policy);

  assert.deepEqual(directives.get('script-src'), ["'self'"]);
  assert.deepEqual(directives.get('style-src'), ["'self'", `'nonce-${VALID_NONCE}'`]);
  assert.equal(policy.split(`'nonce-${VALID_NONCE}'`).length - 1, 1);
});

test('the policy has no inline/eval/wildcard/bypass source and enables Trusted Types', () => {
  const policy = buildCsp({ nonce: VALID_NONCE, connectSrc: ['https://api.example.com'] });

  for (const forbidden of [
    "'unsafe-inline'",
    "'unsafe-eval'",
    "'unsafe-hashes'",
    "'strict-dynamic'",
    'angular#unsafe-bypass',
  ]) {
    assert.ok(!policy.includes(forbidden), `${forbidden} must not appear in: ${policy}`);
  }
  assert.ok(!policy.includes('*'), `no wildcard source anywhere: ${policy}`);
  assert.ok(policy.includes("require-trusted-types-for 'script'"));
  assert.ok(policy.includes('trusted-types angular'));
  assert.ok(policy.includes("frame-ancestors 'none'"));
});

test('generateNonce: 200 fresh nonces, each 16 bytes of standard base64', () => {
  const seen = new Set();
  for (let index = 0; index < 200; index += 1) {
    const nonce = generateNonce();
    assert.ok(isValidNonce(nonce), `generated nonce must be valid: ${nonce}`);
    assert.equal(nonce.length, 24);
    assert.equal(Buffer.from(nonce, 'base64').length, 16);
    seen.add(nonce);
  }
  assert.equal(seen.size, 200, 'nonces must never repeat');
});

test('isValidNonce accepts only 22 base64 characters plus ==', () => {
  assert.ok(isValidNonce(VALID_NONCE));
  // The full standard alphabet, upper/lower/digits/'+'/'/', is legal.
  assert.ok(isValidNonce('AbC+d/efGhIjKlMnOpQrS+=='));

  const rejected = [
    '',
    'abc==',
    'A'.repeat(30),
    `${'A'.repeat(24)}=`,
    'AAAA-_AAAAAAAAAAAAAAAA==',
    null,
    undefined,
    42,
    {},
    [],
    Buffer.from('A'.repeat(16)),
  ];
  for (const value of rejected) {
    assert.equal(isValidNonce(value), false, `must reject ${String(value)}`);
  }
});

test('buildCsp refuses an invalid nonce', () => {
  for (const nonce of ['', 'short', 'AAAA-_AAAAAAAAAAAAAAAA==', '+'.repeat(22), undefined]) {
    assert.throws(() => buildCsp({ nonce }), /nonce/);
  }
  assert.throws(() => buildCsp(), /nonce/);
});

test('connectSrc appends validated origins after self and deduplicates them', () => {
  const policy = buildCsp({
    nonce: VALID_NONCE,
    connectSrc: ['https://api.example.com', 'wss://x.example.com:8443', 'https://api.example.com'],
  });

  assert.equal(
    splitPolicy(policy).get('connect-src').join(' '),
    "'self' https://api.example.com wss://x.example.com:8443",
  );
  assert.equal(policy.split('https://api.example.com').length - 1, 1);
});

test('connectSrc rejects anything but a bare https:/wss: origin, naming the entry', () => {
  const rejected = [
    '*',
    'http://a.com',
    'https://a.com/path',
    'https://a.com; script-src *',
    "'unsafe-inline'",
    'data:',
    'https://',
    'a.com',
    'https://a..b',
    'https://a.example.com.',
    'https://-a.example.com',
    'https://a.example.com:',
    'https://a.example.com:0',
    'https://a.example.com:65536',
    'https://a.example.com:99999',
  ];
  for (const entry of rejected) {
    assertThrowsNaming(entry, () => buildCsp({ nonce: VALID_NONCE, connectSrc: [entry] }));
  }
});

test('connectSrc normalises the scheme and host to lower case', () => {
  const policy = buildCsp({
    nonce: VALID_NONCE,
    connectSrc: ['HTTPS://API.Example.COM', 'WSS://X.Example.COM:8443'],
  });
  // A CSP source is case-insensitive, so the header carries one canonical spelling: the emitted
  // bytes never depend on the casing the deploy environment used.
  assert.equal(
    splitPolicy(policy).get('connect-src').join(' '),
    "'self' https://api.example.com wss://x.example.com:8443",
  );

  // Two spellings of one origin are one source, not two entries in the header.
  const deduplicated = buildCsp({
    nonce: VALID_NONCE,
    connectSrc: ['https://api.example.com', 'HTTPS://api.example.com'],
  });
  assert.equal(
    splitPolicy(deduplicated).get('connect-src').join(' '),
    "'self' https://api.example.com",
  );
});

test('buildCsp refuses connectSrc extras for a policy without a connect-src directive', () => {
  assert.throws(
    () => assertConnectSrcAppendable({ 'img-src': ["'self'"] }, ['https://api.example.com']),
    /connect-src/,
  );
  // No extras means nothing to drop, so a policy without connect-src stays valid.
  assert.deepEqual(assertConnectSrcAppendable({ 'img-src': ["'self'"] }, []), []);
  const extras = ['https://a.b'];
  assert.deepEqual(assertConnectSrcAppendable({ 'connect-src': ["'self'"] }, extras), extras);
});

test('parseConnectSrc splits comma and whitespace separated allowlists', () => {
  assert.deepEqual(parseConnectSrc('https://api.example.com, wss://x.example.com:8443'), [
    'https://api.example.com',
    'wss://x.example.com:8443',
  ]);
  assert.deepEqual(parseConnectSrc('  https://a.example.com \n\t,  wss://b.example.com '), [
    'https://a.example.com',
    'wss://b.example.com',
  ]);
  assert.deepEqual(parseConnectSrc(''), []);
  assert.deepEqual(parseConnectSrc(undefined), []);
  assert.deepEqual(parseConnectSrc(null), []);

  assertThrowsNaming('http://a.com', () => parseConnectSrc('https://ok.example.com http://a.com'));
  assertThrowsNaming('*', () => parseConnectSrc('*'));
  assertThrowsNaming('https://a.com/path', () => parseConnectSrc('https://a.com/path'));
});

test('assertStrictPolicy rejects each forbidden source in an edited policy', () => {
  assert.equal(assertStrictPolicy(EXPECTED_POLICY), EXPECTED_POLICY);

  const edited = [
    "default-src 'none'; script-src 'unsafe-inline'",
    "default-src 'none'; script-src 'unsafe-eval'",
    "script-src 'unsafe-hashes'",
    "script-src 'strict-dynamic'",
    'connect-src *',
    'trusted-types angular angular#unsafe-bypass',
    'trusted-types angular#unsafe-bypass',
  ];
  for (const policy of edited) {
    assert.throws(() => assertStrictPolicy(policy), /forbidden source/);
  }
});

test('assertStrictPolicy keeps the nonce inside style-src', () => {
  assert.equal(assertStrictPolicy(EXPECTED_POLICY, VALID_NONCE), EXPECTED_POLICY);

  assert.throws(
    () => assertStrictPolicy(`script-src 'self' 'nonce-${VALID_NONCE}'`, VALID_NONCE),
    /style-src/,
  );
  assert.throws(
    () => assertStrictPolicy("default-src 'none'; style-src 'self'", VALID_NONCE),
    /style-src/,
  );
});

test('assertStrictPolicy compares forbidden sources case-insensitively', () => {
  // A browser matches a CSP keyword case-insensitively, so a hand-edited policy that spells the
  // escape hatch `'Unsafe-Inline'` would be honoured there and must not pass this guard.
  for (const policy of [
    "default-src 'none'; script-src 'Unsafe-Inline'",
    "default-src 'none'; script-src 'UNSAFE-EVAL'",
    "default-src 'none'; script-src 'Strict-Dynamic'",
    "connect-src *; script-src 'self'",
    "trusted-types Angular#Unsafe-Bypass; script-src 'self'",
  ]) {
    assert.throws(() => assertStrictPolicy(policy), /forbidden source/);
  }
  // A CSP keyword is matched case-insensitively by the browser, so a differently-cased `'self'`
  // is the same single source and stays allowed by the exact-source rule.
  const trustedTypes = "; require-trusted-types-for 'script'; trusted-types angular";
  const caseVaried = `script-src 'Self'${trustedTypes}`;
  assert.equal(assertStrictPolicy(caseVaried), caseVaried);
});

test('assertStrictPolicy requires Trusted Types to stay enforced', () => {
  const base = "default-src 'none'; script-src 'self'; style-src 'self'";
  const requireScript = "; require-trusted-types-for 'script'";
  assert.throws(() => assertStrictPolicy(base), /require-trusted-types-for/);
  // The directive alone is not enough: without `trusted-types`, every policy name is free.
  assert.throws(() => assertStrictPolicy(`${base}${requireScript}`), /trusted-types directive/);
  // Only `'script'` counts: any other value would leave the DOM sinks this policy closes open.
  assert.throws(
    () => assertStrictPolicy(`${base}; require-trusted-types-for 'style'`),
    /require-trusted-types-for/,
  );
  const complete = `${base}${requireScript}; trusted-types angular`;
  assert.equal(assertStrictPolicy(complete), complete);
});

test('assertStrictPolicy keeps script-src at exactly self', () => {
  const trustedTypes = "; require-trusted-types-for 'script'; trusted-types angular";
  for (const scriptSrc of [
    "'self' data:",
    "'self' blob:",
    'blob:',
    'data:',
    'https:',
    'http:',
    "'self' https://cdn.example.com",
    'https://cdn.example.com',
  ]) {
    assert.throws(
      () => assertStrictPolicy(`default-src 'none'; script-src ${scriptSrc}${trustedTypes}`),
      /script-src must be exactly 'self'/,
    );
  }
  assert.throws(
    () => assertStrictPolicy(`default-src 'none'${trustedTypes}`),
    /script-src must be exactly 'self'/,
  );
  const allowed = `script-src 'self'${trustedTypes}`;
  assert.equal(assertStrictPolicy(allowed), allowed);
});

test('injectNonce replaces every placeholder and leaves the rest byte-identical', () => {
  const html =
    '<app-root ngCspNonce="__CSP_NONCE__"></app-root>' +
    '<style nonce="__CSP_NONCE__">:root { --x: 1 }</style>';
  const injected = injectNonce(html, VALID_NONCE);

  assert.equal(injected.split(NONCE_PLACEHOLDER).length - 1, 0);
  assert.equal(injected, html.split(NONCE_PLACEHOLDER).join(VALID_NONCE));
});

test('injectNonce does not expand $ patterns from surrounding markup', () => {
  // A replacement *string* would expand `$&` and `$1`; a replacer function keeps them literal.
  const before = '<p>$& $1 $$ $` $&amp;</p>';
  const html = `${before}<app-root ngCspNonce="__CSP_NONCE__"></app-root>`;

  assert.equal(
    injectNonce(html, VALID_NONCE),
    `${before}<app-root ngCspNonce="${VALID_NONCE}"></app-root>`,
  );
});

test('injectNonce throws on an invalid nonce and passes html without placeholders through', () => {
  const html = '<app-root ngCspNonce="__CSP_NONCE__"></app-root>';
  assert.throws(() => injectNonce(html, 'nope'), /nonce/);

  const plain = '<app-root></app-root>';
  assert.equal(injectNonce(plain, VALID_NONCE), plain);
});

test('src/index.html carries the nonce placeholder on the app-root element', () => {
  const html = readFileSync(INDEX_HTML_PATH, 'utf8');

  // Only the attribute is asserted: its count is an incidental property of the template, while
  // `ngCspNonce` on the app root is what Angular reads to authorise the styles it writes.
  assert.match(html, /<app-root\b[^>]*\sngCspNonce="__CSP_NONCE__"[^>]*>/);
});

test('angular.json disables SRI and critical-path inlining and never enables autoCsp', () => {
  const angular = readJson(ANGULAR_JSON_PATH);
  const build = angular.projects.web.architect.build;

  assert.equal(build.options.subresourceIntegrity, false);
  assert.equal(build.configurations.production.optimization.styles.inlineCritical, false);
  assert.equal(containsKey(angular, 'autoCsp'), false);
});

test('the deploy JSON keeps the nonce marker the edge substitutes', () => {
  // The one JSON property the module does NOT assert at import: `csp.mjs` requires only a
  // non-empty marker and substitutes whatever it finds, so a renamed marker would leave every
  // header in this suite byte-identical while the deployed edge — which replaces this exact
  // token out of the same file (10.2.1-T1.4) — served it verbatim in `style-src`.
  assert.equal(readJson(POLICY_JSON_PATH).nonce_marker, '{nonce}');
});
