// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, ids
// only): every string that leaves the app as telemetry passes through here first.
//
// Two layers, because they answer different questions. `scrubText` is the content filter: it
// removes credentials, cookies, tokens, e-mail addresses, identifiers and URL query strings
// from free text, whatever shape it arrives in, and reduces every URL and every bare API path
// to a path built from the recorded API words and `:id`. `scrubAttributes` is the schema
// filter: it drops every attribute key that `attribute-allowlist.ts` does not list and then
// applies that key's own rule, so a key that *is* allowlisted still cannot smuggle a value the
// key is not supposed to carry (a route template holding a concrete id, a locale holding a
// sentence).
//
// Both are pure, total and non-throwing: telemetry loss must never break the app, so a value
// the rules cannot understand is dropped rather than reported.
//
// Known limits, by construction rather than by omission:
// - an org or project *name* written as a bare word in prose ("the Acme project failed") is
//   indistinguishable from an ordinary noun, so no regular expression removes it; only the
//   positions a grammar marks — URLs, API paths, headers, `key=value` pairs, quotes — are
//   filtered;
// - a route path whose ids are plain words keeps those words: in `/projects/acme-corp/runs/
//   p-1` the `p-1` becomes `:id` but `acme-corp` survives, because a static route word and a
//   slug have the same shape. Concrete literals are the router's business: `route-template.ts`
//   builds the template from `routeConfig.path` and never reads a parameter value;
// - the input is cut to a fixed window before any rule runs (see `MAX_SCAN_LENGTH`), so a
//   secret that begins inside the last few characters of that window is cut, not recognised.
import type { Attributes } from '@opentelemetry/api';

import { API_BASE_PATH } from '../http/api-origin';
import { KNOWN_API_SEGMENTS } from './api-path-template';
import { ALLOWED_ATTRIBUTE_KEYS } from './attribute-allowlist';

/** Default cap for free text (an error message is one line, not a stack trace). */
const DEFAULT_MAX_TEXT_LENGTH = 200;

/**
 * How much of the input any rule ever sees: `4 x` the requested cap, clamped into
 * `[MIN_SCAN_LENGTH, MAX_SCAN_LENGTH]`. A pattern with an unbounded prefix over an unbounded
 * string is quadratic (`/[A-Za-z0-9]+@/` on 200 000 letters takes seconds), so the window is
 * fixed *before* the first pattern runs, and every quantifier below is bounded as well. No
 * caller needs more than this: the result is truncated to `maxLength` anyway.
 */
const MIN_SCAN_LENGTH = 512;
const MAX_SCAN_LENGTH = 2048;
const SCAN_LENGTH_FACTOR = 4;

/** A route template longer than this is not a route template. */
const MAX_ROUTE_LENGTH = 300;

const REDACTED = '[redacted]';
const QUOTED = '[quoted]';
const EMAIL = '[email]';
const ID = ':id';

/** A value the SDK can serialize; anything else (object, array, `undefined`) is dropped. */
type SimpleValue = string | number | boolean;

/** One attribute key's rule: the cleaned value, or `undefined` to drop the key. */
type AttributeRule = (value: SimpleValue) => SimpleValue | undefined;

interface TextRule {
  /** What the rule removes, for the order-of-application comments below. */
  readonly removes: string;
  readonly apply: (value: string) => string;
}

/** The segments of the API base path (`/v1`): a version prefix is not an id. */
const BASE_PATH_SEGMENTS: ReadonlySet<string> = new Set(
  API_BASE_PATH.split('/').filter((segment) => segment !== ''),
);

/** Trailing punctuation belongs to the sentence around a URL, not to the URL (`…/runs: 404`). */
const TRAILING_PUNCTUATION = /[.,;:!?]+$/;

/**
 * `Authorization: Bearer …`, `Cookie: …`, `Set-Cookie: …`, `X-XSRF-TOKEN: …`, `X-CSRF: …` —
 * the whole value goes, because a cookie or a CSRF token is a session. Runs first: the later
 * `key: value` rule would otherwise rewrite only the first pair of a `Cookie` header.
 */
const HEADER_VALUE =
  /\b(authorization|proxy-authorization|cookie|set-cookie|x-xsrf-token|x-csrf(?:-token)?)\s*:\s*[^\r\n]{0,2048}/gi;

/** `Bearer <credential>`, `Basic <base64>`, `Token <credential>` — keep the scheme, drop the secret. */
const CREDENTIAL = /\b(bearer|basic|token)\s+[A-Za-z0-9._~+/=-]{8,2048}/gi;

/**
 * An absolute URL: scheme, host, optional userinfo, path, query, fragment. It is never reduced
 * to the host (a host can name the org); it is reduced to its path.
 */
const ABSOLUTE_URL = /\b[a-z][a-z0-9+.-]{0,31}:\/\/[^\s"'`<>]{0,2048}/gi;

/** A path carrying a query string or fragment: keep the path, drop `?…` / `#…`. */
const RELATIVE_URL = /\/[^\s?#"'`<>]{0,2048}[?#][^\s"`<>]{0,2048}/g;

/** A bare API path (`/v1/…`), matched together with the delimiter in front of it. */
const API_PATH = /(^|[\s"'(=,])(\/v1(?:\/[^\s"'`<>?#]{0,512}){0,64})/g;

/**
 * `token=…`, `password: …`, `apiKey: …` and friends — `=` or `:` (a query string, a YAML dump,
 * a framework error body), an optional matching quote pair, and a value that is a secret
 * whatever it looks like. Ordinary prose has no separator, so `token expired` is untouched.
 */
const SECRET_PAIR =
  /\b(token|secret|password|passwd|pwd|api[_-]?key|apikey|key|session|sid|code|state|auth|signature)\b(\s*[:=]\s*)(["'`]?)([^\s&,;"'`]{1,2048})\3/gi;

/** A JWT-shaped triple (`header.payload.signature`), which no allowlisted field ever needs. */
const JWT_SHAPED = /\b[A-Za-z0-9_-]{8,512}\.[A-Za-z0-9_-]{8,512}\.[A-Za-z0-9_-]{8,512}\b/g;

/**
 * An e-mail address: the one identifier a log line is most likely to carry by accident. The
 * local part and every domain label are bounded, so the pattern is linear; the left boundary is
 * a plain capture rather than a lookbehind; and both sides accept Unicode letters and digits, so
 * `пользователь@example.test` and a Cyrillic homoglyph in `аdmin@example.test` are recognised.
 */
const EMAIL_ADDRESS =
  /(^|[^\p{L}\p{N}._%+'-])([\p{L}\p{N}._%+'-]{1,64}@[\p{L}\p{N}-]{1,63}(?:\.[\p{L}\p{N}-]{1,63}){0,20}\.[\p{L}]{2,24})/gu;

/** A UUID: an id, so the placeholder keeps the shape (`:id`) without the value. */
const UUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;

/** A 16+ hex run (trace ids, sha prefixes, opaque tokens). */
const HEX_RUN = /\b[0-9a-f]{16,2048}\b/gi;

/** A long base64/base64url run: an encoded secret or key. */
const BASE64_RUN = /\b[A-Za-z0-9+/_-]{24,2048}={0,2}/g;

/** A 6+ digit number: an account, run or document number, never a status or a vital. */
const LONG_DIGITS = /\b\d{6,2048}\b/g;

/** A quoted fragment (`"Julia"`, `'run 42'`, `` `abc` ``): a name or a value, not an id. */
const QUOTED_SUBSTRING = /"[^"]{0,2048}"|'[^']{0,2048}'|`[^`]{0,2048}`/g;

/** Any quote left after `QUOTED_SUBSTRING` ran opens a string that was never closed. */
const ANY_QUOTE = /["'`]/;

/**
 * The path with every segment that is not a known API word replaced by `:id`: the words of
 * `KNOWN_API_SEGMENTS` stay (they are the recorded contract, see `api-path-template.ts`) and so
 * do the segments of `API_BASE_PATH`. Idempotent: `:id` is not a known word, so a path that is
 * already a template comes back unchanged. Trailing punctuation belongs to the sentence around
 * the path (`…/runs: 404`), so it is taken off before the segments are read and put back after.
 */
function reducePath(pathname: string): string {
  const trimmed = pathname.replace(TRAILING_PUNCTUATION, '');
  const trailing = pathname.slice(trimmed.length);
  const segments = trimmed.split('/').filter((segment) => segment !== '');
  if (segments.length === 0) {
    return `/${trailing}`;
  }
  const templated = segments.map((segment) =>
    BASE_PATH_SEGMENTS.has(segment) || KNOWN_API_SEGMENTS.has(segment) ? segment : ID,
  );
  return `/${templated.join('/')}${trailing}`;
}

/**
 * An absolute URL reduced to its path: never the host (a host can name the org), never
 * userinfo, query or fragment, and every id in the path becomes `:id`.
 */
function absoluteUrlPath(match: string): string {
  try {
    const { pathname } = new URL(match);
    return reducePath(pathname);
  } catch {
    return REDACTED;
  }
}

/**
 * Applied in this order, on purpose: headers before pairs (a `Cookie` header is a list of
 * pairs), URLs and paths before pairs (a query string is a list of pairs), pairs before
 * identifiers (a JWT would otherwise be rewritten fragment by fragment), identifiers before the
 * generic base64 rule, digits last (an e-mail or a UUID has to be whole when its own rule sees
 * it), and quoted names after everything else but still before the cap, so a name that straddles
 * the cap is removed rather than half-kept.
 */
const TEXT_RULES: readonly TextRule[] = [
  {
    removes: 'header values (Authorization, Cookie, Set-Cookie, X-XSRF-TOKEN, X-CSRF)',
    apply: (value) => value.replace(HEADER_VALUE, (_match, name: string) => `${name}: ${REDACTED}`),
  },
  {
    removes: 'credential schemes (Bearer, Basic, Token)',
    apply: (value) =>
      value.replace(CREDENTIAL, (_match, scheme: string) => `${scheme} ${REDACTED}`),
  },
  {
    removes: 'absolute URLs (host, userinfo, query, fragment) and the ids in their path',
    apply: (value) => value.replace(ABSOLUTE_URL, (match: string) => absoluteUrlPath(match)),
  },
  {
    removes: 'query strings and fragments on paths',
    apply: (value) =>
      value.replace(RELATIVE_URL, (match: string) => {
        const cut = match.search(/[?#]/);
        return cut === -1 ? match : match.slice(0, cut);
      }),
  },
  {
    removes: 'the ids in a bare API path (/v1/…)',
    apply: (value) =>
      value.replace(API_PATH, (_match, lead: string, path: string) => `${lead}${reducePath(path)}`),
  },
  {
    removes: 'secret key=value and key: value pairs',
    apply: (value) =>
      value.replace(
        SECRET_PAIR,
        (_match, name: string, separator: string) => `${name}${separator}${REDACTED}`,
      ),
  },
  {
    removes: 'JWT-shaped strings',
    apply: (value) => value.replace(JWT_SHAPED, REDACTED),
  },
  {
    removes: 'e-mail addresses',
    apply: (value) => value.replace(EMAIL_ADDRESS, (_match, lead: string) => `${lead}${EMAIL}`),
  },
  {
    removes: 'UUIDs',
    apply: (value) => value.replace(UUID, ID),
  },
  {
    removes: '16+ character hex runs',
    apply: (value) => value.replace(HEX_RUN, ID),
  },
  {
    removes: '24+ character base64 runs',
    apply: (value) => value.replace(BASE64_RUN, REDACTED),
  },
  {
    removes: '6+ digit numbers',
    apply: (value) => value.replace(LONG_DIGITS, ID),
  },
  {
    removes: 'quoted names, and everything after an unterminated quote',
    apply: (value) => {
      const quoted = value.replace(QUOTED_SUBSTRING, QUOTED);
      const unterminated = quoted.search(ANY_QUOTE);
      return unterminated === -1 ? quoted : quoted.slice(0, unterminated).trimEnd();
    },
  },
];

/** A `:param` — an Angular route parameter; the only template syntax besides `**`. */
const PARAM_SEGMENT = /^:[a-z][A-Za-z0-9_]{0,63}$/;

/** A plain lowercase route word (`projects`, `runs`) — no digits, because a digit means an id. */
const STATIC_WORD_SEGMENT = /^[a-z][a-z-]{0,31}$/;

/** A version prefix (`v1`): fixed, not an id, and not a word either. */
const VERSION_SEGMENT = /^v[1-9]\d{0,2}$/;

const WILDCARD_SEGMENT = '**';

const UUID_VALUE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ERROR_CODE = /^[a-z][a-z0-9_]{0,63}$/;
const ERROR_KIND = /^[a-z_]{1,32}$/;
const ERROR_TYPE = /^[A-Za-z][A-Za-z0-9_$.]{0,127}$/;
const METHOD = /^[A-Z]{1,16}$/;
const NAVIGATION_TYPE = /^[a-z][a-z-]{0,31}$/;

const MIN_STATUS_CODE = 100;
const MAX_STATUS_CODE = 599;

/**
 * The per-key rules. Every key of `ALLOWED_ATTRIBUTE_KEYS` has one, and `telemetry.spec.ts`
 * proves that: an allowlisted key accepted a value in the spec, so a key without a rule here
 * fails the test instead of silently vanishing from every span.
 */
const ATTRIBUTE_RULES: Readonly<Record<string, AttributeRule>> = {
  'http.request.method': (value) => (isText(value) && METHOD.test(value) ? value : undefined),
  'http.response.status_code': (value) =>
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= MIN_STATUS_CODE &&
    value <= MAX_STATUS_CODE
      ? value
      : undefined,
  'http.route': (value) => (isText(value) ? routeTemplateValue(value) : undefined),
  'evidgate.route': (value) => (isText(value) ? routeTemplateValue(value) : undefined),
  'evidgate.outcome': (value) =>
    value === 'ok' || value === 'cancelled' || value === 'error' ? value : undefined,
  'evidgate.request_id': (value) =>
    isText(value) && UUID_VALUE.test(value) ? value.toLowerCase() : undefined,
  'evidgate.locale': (value) => (value === 'en' || value === 'vi' ? value : undefined),
  'evidgate.api.error_code': (value) =>
    isText(value) && ERROR_CODE.test(value) ? value : undefined,
  'evidgate.api.error_kind': (value) =>
    isText(value) && ERROR_KIND.test(value) ? value : undefined,
  'evidgate.api.retryable': (value) => (typeof value === 'boolean' ? value : undefined),
  'evidgate.error.source': (value) =>
    value === 'window.error' ||
    value === 'unhandledrejection' ||
    value === 'angular' ||
    value === 'api'
      ? value
      : undefined,
  'error.type': (value) => (isText(value) && ERROR_TYPE.test(value) ? value : undefined),
  'error.message': freeText(DEFAULT_MAX_TEXT_LENGTH),
  'web_vital.name': (value) =>
    value === 'LCP' || value === 'INP' || value === 'CLS' ? value : undefined,
  'web_vital.value': (value) =>
    typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined,
  'web_vital.rating': (value) =>
    value === 'good' || value === 'needs-improvement' || value === 'poor' ? value : undefined,
  'web_vital.navigation_type': (value) =>
    isText(value) && NAVIGATION_TYPE.test(value) ? value : undefined,
};

function isText(value: SimpleValue): value is string {
  return typeof value === 'string';
}

/** A value the SDK can serialize: string, boolean, or a finite number. */
function isSimpleValue(value: unknown): value is SimpleValue {
  if (typeof value === 'string' || typeof value === 'boolean') {
    return true;
  }
  return typeof value === 'number' && Number.isFinite(value);
}

/** Free text: the content rules, quoted names and the cap included. */
function freeText(maxLength: number): AttributeRule {
  return (value) => (isText(value) ? scrubText(value, maxLength) : undefined);
}

/**
 * A route template, or `undefined` when the value is not path-shaped at all. A segment stays
 * only when it *is* template syntax — a `:param`, `**`, a version prefix — or a plain lowercase
 * word (`projects`, `runs`). Any digit makes a segment an id, so a concrete
 * `/projects/acme-corp/runs/p-1` leaves as `/projects/acme-corp/runs/:id`: the id goes, while a
 * slug made of plain words keeps its words, because nothing tells it apart from a static route
 * word. Concrete literal segments are the router's business (`route-template.ts`).
 */
function routeTemplateValue(value: string): string | undefined {
  if (!value.startsWith('/') || value.length > MAX_ROUTE_LENGTH) {
    return undefined;
  }
  if (value.includes('?') || value.includes('#')) {
    return undefined;
  }
  const segments = value.split('/').filter((segment) => segment !== '');
  if (segments.length === 0) {
    return value === '/' ? '/' : undefined;
  }
  const templated = segments.map((segment) => {
    const isTemplate =
      segment === WILDCARD_SEGMENT ||
      PARAM_SEGMENT.test(segment) ||
      VERSION_SEGMENT.test(segment) ||
      STATIC_WORD_SEGMENT.test(segment);
    return isTemplate ? segment : ID;
  });
  return `/${templated.join('/')}`;
}

/**
 * The window without its last, possibly cut, whitespace-delimited token. A window with no
 * whitespace at all is one token and stays whole: the rules reduce a run like that to `:id`.
 */
function withoutCutToken(window: string): string {
  let end = window.length;
  while (end > 0 && /\s/.test(window.charAt(end - 1)) === false) {
    end -= 1;
  }
  return end === 0 ? window : window.slice(0, end);
}

/**
 * Removes credentials, identifiers, URL query strings and the ids in URLs and API paths from
 * one line of free text, and truncates the result to `maxLength` (default 200) characters. The
 * input is cut to a bounded window before any rule runs, so the cost is a function of
 * `maxLength`, never of the input length; the token the window cuts in half is dropped, because
 * half a secret can be too short for any rule to recognise (the tail would then reach the output
 * once earlier URLs and ids shrink). Pure and non-throwing: any input produces a string.
 */
export function scrubText(value: string, maxLength: number = DEFAULT_MAX_TEXT_LENGTH): string {
  const limit = Number.isFinite(maxLength)
    ? Math.max(0, Math.trunc(maxLength))
    : DEFAULT_MAX_TEXT_LENGTH;
  const scanLength = Math.min(
    Math.max(limit * SCAN_LENGTH_FACTOR, MIN_SCAN_LENGTH),
    MAX_SCAN_LENGTH,
  );
  let text = value.length > scanLength ? withoutCutToken(value.slice(0, scanLength)) : value;
  for (const rule of TEXT_RULES) {
    text = rule.apply(text);
  }
  return text.length > limit ? text.slice(0, limit) : text;
}

/**
 * The scrubbed copy of a span's attributes: keys outside `ALLOWED_ATTRIBUTE_KEYS` are dropped,
 * values that are not a finite primitive are dropped, and every kept key is then held to its own
 * rule. The input is never mutated.
 */
export function scrubAttributes(attributes: Attributes): Attributes {
  const scrubbed: Attributes = {};
  for (const [key, value] of Object.entries(attributes)) {
    if (ALLOWED_ATTRIBUTE_KEYS.has(key) === false || isSimpleValue(value) === false) {
      continue;
    }
    const rule = ATTRIBUTE_RULES[key];
    if (rule === undefined) {
      continue;
    }
    const result = rule(value);
    if (result !== undefined) {
      scrubbed[key] = result;
    }
  }
  return scrubbed;
}
