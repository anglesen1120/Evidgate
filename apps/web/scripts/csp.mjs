// 09.1.1-T10 (AC7, ADR-0008 rule 4): the app-origin Content Security Policy is computed here
// and nowhere else.
//
// `app-origin-csp.json` next to this file is the deploy-time source of truth: the edge consumes
// the same JSON, so a directive cannot drift between what the app builds and what the CDN sends.
// This module is the only code that turns that JSON into a header value, and it refuses the parts
// of a policy that would make the app origin unfaithful to the ADR: inline/eval script sources,
// `strict-dynamic`, a wildcard source, and the `angular#unsafe-bypass` Trusted Types policy name
// that would let `bypassSecurityTrust*` re-enable HTML injection. Omitting the bypass name is
// decision D-S66-Q1: a bypass call then fails in the browser as well as in lint.
//
// The nonce is generated per response and is written into the HTML body only (`injectNonce`),
// plus that response's own style-src directive (Angular emits its component styles with the
// nonce it reads from `<app-root ngCspNonce>`). `script-src` deliberately has no nonce at all:
// the build ships no inline scripts (`subresourceIntegrity: false` and
// `optimization.styles.inlineCritical: false` in angular.json keep it that way), so a nonce there
// would only widen the policy.
//
// Dependency-free on purpose (node:crypto + node:fs only), like the other apps/web scripts, so a
// `npm ci --ignore-scripts` CI install needs nothing extra.
import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';

/** Where the HTML template and the preview server expect the nonce to be written. */
export const NONCE_PLACEHOLDER = '__CSP_NONCE__';

/** 16 bytes of entropy: 22 base64 characters plus `==`, i.e. >= 128 bits (a CSP3 nonce-source). */
const NONCE_PATTERN = /^[A-Za-z0-9+/]{22}==$/;

/**
 * The only shape a `connect-src` extension may take: a bare https:/wss: origin with an optional
 * port, no path, no wildcard, no comma and no `;`. Every host label must be non-empty (`a..b` is
 * refused, as is a label that starts or ends with `-`), and the port is range-checked after the
 * match, because `\d{1,5}` alone would also accept `:0` and `:99999`. Anything else is either a
 * no-op in the browser or a way to smuggle a second directive into the header, so it throws with
 * the entry named.
 */
const ORIGIN_PATTERN =
  /^(https|wss):\/\/([a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*)(?::(\d{1,5}))?$/i;

/** The highest port a URL may name; the origin regex has already limited it to at most 5 digits. */
const MAX_PORT = 65_535;

/**
 * Source expressions that must never reach the app origin. `unsafe-inline`, `unsafe-eval`,
 * `unsafe-hashes` and `strict-dynamic` re-enable injected script; `*` opens the policy to any
 * host; `angular#unsafe-bypass` would let Angular's `bypassSecurityTrust*` APIs defeat Trusted
 * Types by design.
 */
const FORBIDDEN_SOURCES = new Set([
  "'unsafe-inline'",
  "'unsafe-eval'",
  "'unsafe-hashes'",
  "'strict-dynamic'",
  '*',
  'angular#unsafe-bypass',
]);

const POLICY = JSON.parse(readFileSync(new URL('./app-origin-csp.json', import.meta.url), 'utf8'));

if (POLICY.nonce_placeholder !== NONCE_PLACEHOLDER) {
  throw new Error(
    `CSP policy nonce_placeholder is ${JSON.stringify(POLICY.nonce_placeholder)}, ` +
      `expected ${JSON.stringify(NONCE_PLACEHOLDER)}`,
  );
}
if (typeof POLICY.nonce_marker !== 'string' || POLICY.nonce_marker === '') {
  throw new Error('CSP policy nonce_marker must be a non-empty string');
}
if (POLICY.directives === null || typeof POLICY.directives !== 'object') {
  throw new Error('CSP policy directives must be an object mapping a directive to string[]');
}
for (const [name, sources] of Object.entries(POLICY.directives)) {
  if (!Array.isArray(sources) || sources.length === 0) {
    throw new Error(`CSP policy directive ${name} must be a non-empty array`);
  }
  if (sources.some((source) => typeof source !== 'string' || source === '')) {
    throw new Error(`CSP policy directive ${name} must hold non-empty strings`);
  }
}

const NONCE_MARKER = POLICY.nonce_marker;

/** A fresh 128-bit nonce, standard base64 (22 characters plus `==`). Never cached. */
export function generateNonce() {
  return randomBytes(16).toString('base64');
}

/** `true` only for a >= 128-bit standard-base64 nonce; url-safe base64 and padding drift fail. */
export function isValidNonce(value) {
  return typeof value === 'string' && NONCE_PATTERN.test(value);
}

function assertNonce(nonce) {
  if (!isValidNonce(nonce)) {
    throw new Error(
      `invalid CSP nonce ${JSON.stringify(nonce)}: expected 16 base64-encoded bytes ` +
        '(22 characters plus "==")',
    );
  }
  return nonce;
}

function assertConnectSrcOrigin(entry) {
  const match = typeof entry === 'string' ? ORIGIN_PATTERN.exec(entry) : null;
  if (match === null) {
    throw new Error(
      `invalid connect-src entry ${JSON.stringify(entry)}: expected a bare https: or wss: ` +
        'origin such as https://api.example.com',
    );
  }
  // The regex caps the digit count, not the value: `:0` and `:99999` both match it and neither
  // is a port a browser can connect to, so the range is checked here rather than widened into it.
  const port = match[3];
  if (port !== undefined && (Number(port) < 1 || Number(port) > MAX_PORT)) {
    throw new Error(
      `invalid connect-src entry ${JSON.stringify(entry)}: port must be 1-${String(MAX_PORT)}`,
    );
  }
  // Scheme and host are case-insensitive in a CSP source, so the header carries the canonical
  // lower-case form: two spellings of one origin are one entry, and the emitted header does not
  // depend on the casing the deploy environment happened to use. The port is digits, which
  // lower-casing leaves alone.
  return entry.toLowerCase();
}

/**
 * Parse `EVIDGATE_WEB_CONNECT_SRC`: a comma and/or whitespace separated origin list. Empty or
 * absent means "no extra origins"; any other shape throws naming the offending entry, so a typo
 * fails the deploy instead of silently producing a policy with a dead source.
 */
export function parseConnectSrc(value) {
  if (value === undefined || value === null) return [];
  if (typeof value !== 'string') {
    throw new Error(`EVIDGATE_WEB_CONNECT_SRC must be a string, got ${typeof value}`);
  }
  const entries = value.split(/[\s,]+/).filter((entry) => entry !== '');
  // The validated entries are returned in their canonical lower-case form, so what the caller
  // hands to `buildCsp` is already what the header will carry.
  return entries.map((entry) => assertConnectSrcOrigin(entry));
}

function parseDirectives(policy) {
  return policy
    .split(';')
    .map((part) => part.trim())
    .filter((part) => part !== '')
    .map((part) => {
      const [name, ...sources] = part.split(/\s+/);
      return { name, sources };
    });
}

/**
 * `directives` is `parseDirectives` output. Directive names are case-insensitive in a browser,
 * so the lookups below compare them lower-case.
 */
function findDirective(directives, name) {
  return directives.find((entry) => entry.name.toLowerCase() === name);
}

/**
 * Trusted Types is not an optional part of this policy: it is what makes `script-src 'self'`
 * enough for a framework that renders templates, and a policy that stopped requiring it would
 * look identical in every other assertion while re-opening the DOM sinks it exists to close.
 */
function assertTrustedTypesPolicy(directives) {
  const required = findDirective(directives, 'require-trusted-types-for');
  if (
    required === undefined ||
    required.sources.length !== 1 ||
    required.sources[0].toLowerCase() !== "'script'"
  ) {
    throw new Error("the CSP policy must declare require-trusted-types-for 'script'");
  }
  const allowed = findDirective(directives, 'trusted-types');
  if (allowed === undefined || allowed.sources.length === 0) {
    throw new Error(
      'the CSP policy must declare a trusted-types directive, or every policy name is free',
    );
  }
  // D-S66-Q1: `angular` is the only policy name the app may create. A hand edit that adds
  // `default`, `angular#bundler` or the bypass name would silently widen the list.
  if (allowed.sources.length !== 1 || allowed.sources[0] !== 'angular') {
    throw new Error('the CSP trusted-types directive must list exactly `angular`');
  }
}

/**
 * `script-src` is exactly `'self'`. A nonce there is unused (the bundle ships no inline script)
 * and any other source — `blob:`, `data:`, an https:/http: origin, a bare host — would be a
 * second place an injected script could come from, so the directive is compared whole instead of
 * source by source.
 */
function assertScriptSrcSelf(directives) {
  const script = findDirective(directives, 'script-src');
  if (
    script === undefined ||
    script.sources.length !== 1 ||
    script.sources[0].toLowerCase() !== "'self'"
  ) {
    const found = script === undefined ? 'no script-src at all' : script.sources.join(' ');
    throw new Error(`script-src must be exactly 'self', found ${found}`);
  }
}

/**
 * The policy's own guardrail. `buildCsp` runs it over every header it produces, so editing
 * `app-origin-csp.json` into an `unsafe-eval` or wildcard policy breaks every build and every
 * preview server rather than silently weakening the app origin. Exported for its own proofs.
 *
 * With `nonce` given it also enforces that the nonce appears in exactly one directive, style-src;
 * either way it enforces Trusted Types and a `script-src` of exactly `'self'`.
 */
export function assertStrictPolicy(policy, nonce) {
  if (typeof policy !== 'string' || policy === '') {
    throw new Error('CSP policy must be a non-empty string');
  }
  const directives = parseDirectives(policy);
  for (const { name, sources } of directives) {
    for (const source of sources) {
      // Case-insensitive, because the browser matches a keyword that way: a hand-edited
      // `'Unsafe-Inline'` resolves to `'unsafe-inline'` there, so it must be refused here.
      if (FORBIDDEN_SOURCES.has(source.toLowerCase())) {
        throw new Error(`CSP directive ${name} carries forbidden source ${source}`);
      }
    }
  }
  if (nonce !== undefined) {
    assertNonce(nonce);
    const carriers = directives.filter(({ sources }) =>
      sources.some((source) => source.includes(nonce)),
    );
    if (carriers.length !== 1 || carriers[0].name !== 'style-src') {
      const found = carriers.map(({ name }) => name).join(', ') || 'no directive';
      throw new Error(`the CSP nonce may appear only in style-src, found in ${found}`);
    }
  }
  assertScriptSrcSelf(directives);
  assertTrustedTypesPolicy(directives);
  return policy;
}

/**
 * An extra `connect-src` origin is only meaningful where the policy has a `connect-src` to append
 * it to; without that directive the loop in `buildCsp` would drop the entry silently, and a
 * deploy that set `EVIDGATE_WEB_CONNECT_SRC` would ship a policy that cannot reach the origin it
 * declared — the exact failure the variable exists to avoid. So that is a throw, not a no-op.
 * Exported for its own proof, like `assertStrictPolicy`.
 */
export function assertConnectSrcAppendable(directives, extras) {
  if (extras.length > 0 && !Object.hasOwn(directives, 'connect-src')) {
    throw new Error(
      'the CSP policy declares no connect-src directive, so these origins cannot be added: ' +
        extras.join(' '),
    );
  }
  return extras;
}

/**
 * Build the header value for one response. Directive order is the JSON's order, so the header is
 * byte-stable for a given nonce; `{nonce}` is substituted in place, and validated `connectSrc`
 * origins are appended after `'self'` (deduplicated, order preserved).
 */
export function buildCsp({ nonce, connectSrc = [] } = {}) {
  assertNonce(nonce);
  if (!Array.isArray(connectSrc)) {
    throw new Error('connectSrc must be an array of https: or wss: origins');
  }
  const extras = [];
  for (const entry of connectSrc) {
    const origin = assertConnectSrcOrigin(entry);
    if (!extras.includes(origin)) extras.push(origin);
  }
  assertConnectSrcAppendable(POLICY.directives, extras);

  const parts = [];
  for (const [name, values] of Object.entries(POLICY.directives)) {
    // A replacer function, never a replacement string: the nonce is validated base64, so a `$`
    // could not appear in it, but the marker substitution must not depend on that.
    const sources = values.map((value) => value.replaceAll(NONCE_MARKER, () => nonce));
    if (name === 'connect-src') {
      for (const extra of extras) {
        if (!sources.includes(extra)) sources.push(extra);
      }
    }
    parts.push(`${name} ${sources.join(' ')}`);
  }

  return assertStrictPolicy(parts.join('; '), nonce);
}

/** The complete header set for an app-origin response: the CSP, under its lowercase name. */
export function buildSecurityHeaders({ nonce, connectSrc = [] } = {}) {
  return { 'content-security-policy': buildCsp({ nonce, connectSrc }) };
}

/**
 * Write `nonce` into every placeholder in an HTML body. Uses a string pattern with a replacer
 * function, so literal `$&` / `$1` text elsewhere in the document survives untouched.
 */
export function injectNonce(html, nonce) {
  assertNonce(nonce);
  if (typeof html !== 'string') {
    throw new Error(`injectNonce needs an HTML string, got ${typeof html}`);
  }
  return html.replaceAll(NONCE_PLACEHOLDER, () => nonce);
}
