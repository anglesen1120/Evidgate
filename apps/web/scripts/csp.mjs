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
 * port, no path, no wildcard, no comma and no `;`. Anything else is either a no-op in the browser
 * or a way to smuggle a second directive into the header, so it throws with the entry named.
 */
const ORIGIN_PATTERN = /^(https|wss):\/\/[a-z0-9]([a-z0-9.-]*[a-z0-9])?(:\d{1,5})?$/i;

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
  if (typeof entry !== 'string' || !ORIGIN_PATTERN.test(entry)) {
    throw new Error(
      `invalid connect-src entry ${JSON.stringify(entry)}: expected a bare https: or wss: ` +
        'origin such as https://api.example.com',
    );
  }
  return entry;
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
  for (const entry of entries) assertConnectSrcOrigin(entry);
  return entries;
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
 * The policy's own guardrail. `buildCsp` runs it over every header it produces, so editing
 * `app-origin-csp.json` into an `unsafe-eval` or wildcard policy breaks every build and every
 * preview server rather than silently weakening the app origin. Exported for its own proofs.
 *
 * With `nonce` given it also enforces that the nonce appears in exactly one directive, style-src.
 */
export function assertStrictPolicy(policy, nonce) {
  if (typeof policy !== 'string' || policy === '') {
    throw new Error('CSP policy must be a non-empty string');
  }
  const directives = parseDirectives(policy);
  for (const { name, sources } of directives) {
    for (const source of sources) {
      if (FORBIDDEN_SOURCES.has(source)) {
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
  return policy;
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
    assertConnectSrcOrigin(entry);
    if (!extras.includes(entry)) extras.push(entry);
  }

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
