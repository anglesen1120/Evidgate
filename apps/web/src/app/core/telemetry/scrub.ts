// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, ids
// only): every string that leaves the app as telemetry passes through here first.
//
// Two layers, because they answer different questions. `scrubText` is the content filter: it
// removes credentials, cookies, tokens, e-mail addresses, identifiers and URL query strings
// from free text, whatever shape it arrives in. `scrubAttributes` is the schema filter: it
// drops every attribute key that `attribute-allowlist.ts` does not list and then applies that
// key's own rule, so a key that *is* allowlisted still cannot smuggle a value the key is not
// supposed to carry (a route template holding a concrete id, a locale holding a sentence).
//
// Both are pure, total and non-throwing: telemetry loss must never break the app, so a value
// the rules cannot understand is dropped rather than reported.
import type { Attributes } from '@opentelemetry/api';

import { ALLOWED_ATTRIBUTE_KEYS } from './attribute-allowlist';

/** Default cap for free text (an error message is one line, not a stack trace). */
const DEFAULT_MAX_TEXT_LENGTH = 200;

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

/**
 * `Authorization: Bearer …`, `Cookie: …`, `Set-Cookie: …`, `X-XSRF-TOKEN: …`, `X-CSRF: …` —
 * the whole value goes, because a cookie or a CSRF token is a session. Runs first: the later
 * `key=value` rule would otherwise rewrite only the first pair of a `Cookie` header.
 */
const HEADER_VALUE =
  /\b(authorization|proxy-authorization|cookie|set-cookie|x-xsrf-token|x-csrf(?:-token)?)\s*:\s*[^\r\n]+/gi;

/** `Bearer <credential>`, `Basic <base64>`, `Token <credential>` — keep the scheme, drop the secret. */
const CREDENTIAL = /\b(bearer|basic|token)\s+[A-Za-z0-9._~+/=-]{8,}/gi;

/**
 * An absolute URL. Reduced to its path (never the host: a host can name the org), and by
 * construction to its path *only*, so userinfo, query and fragment all disappear.
 */
const ABSOLUTE_URL = /\b[a-z][a-z0-9+.-]*:\/\/[^\s"'`<>]+/gi;

/** A path carrying a query string or fragment: keep the path, drop `?…` / `#…`. */
const RELATIVE_URL = /\/[^\s?#"'`<>]*[?#][^\s"`<>]*/g;

/** `token=…`, `password=…`, `api_key=…` and friends: the value is a secret whatever it looks like. */
const SECRET_PAIR =
  /\b(token|secret|password|passwd|pwd|api_key|apikey|key|session|sid|code|state|auth|signature)\s*=\s*[^\s&,;"'`]+/gi;

/** A JWT-shaped triple (`header.payload.signature`), which no allowlisted field ever needs. */
const JWT_SHAPED = /\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g;

/** An e-mail address: the one identifier a log line is most likely to carry by accident. */
const EMAIL_ADDRESS = /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g;

/** A UUID: an id, so the placeholder keeps the shape (`:id`) without the value. */
const UUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;

/** A 16+ hex run (trace ids, sha prefixes, opaque tokens). */
const HEX_RUN = /\b[0-9a-f]{16,}\b/gi;

/** A long base64/base64url run: an encoded secret or key. */
const BASE64_RUN = /\b[A-Za-z0-9+/_-]{24,}={0,2}/g;

/** A 6+ digit number: an account, run or document number, never a status or a vital. */
const LONG_DIGITS = /\b\d{6,}\b/g;

/** A quoted fragment (`"Julia"`, `'run 42'`, `` `abc` ``): a name or a value, not an id. */
const QUOTED_SUBSTRING = /"[^"]*"|'[^']*'|`[^`]*`/g;

/**
 * Applied in this order, on purpose: headers before pairs (a `Cookie` header is a list of
 * pairs), URLs before pairs (a query string is a list of pairs), identifiers before the
 * generic base64 rule (a JWT would otherwise be rewritten fragment by fragment), and digits
 * last (an e-mail or a UUID has to be whole when its own rule sees it).
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
    removes: 'absolute URLs (host, userinfo, query, fragment)',
    apply: (value) =>
      value.replace(ABSOLUTE_URL, (match: string) => {
        try {
          const { pathname } = new URL(match);
          return pathname === '' ? '/' : pathname;
        } catch {
          return REDACTED;
        }
      }),
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
    removes: 'secret key=value pairs',
    apply: (value) => value.replace(SECRET_PAIR, (_match, name: string) => `${name}=${REDACTED}`),
  },
  {
    removes: 'JWT-shaped strings',
    apply: (value) => value.replace(JWT_SHAPED, REDACTED),
  },
  {
    removes: 'e-mail addresses',
    apply: (value) => value.replace(EMAIL_ADDRESS, EMAIL),
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
];

/** `:param` — an Angular route parameter; the only thing besides a static word a template holds. */
const PARAM_SEGMENT = /^:[A-Za-z][A-Za-z0-9_]{0,63}$/;

/** A static path word of the API surface (lowercase, dotted/dashed/hyphenated). */
const LITERAL_SEGMENT = /^[a-z0-9][a-z0-9._-]{0,63}$/;

const UUID_SEGMENT = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HEX_SEGMENT = /^[0-9a-f]{16,}$/i;
const DIGITS_SEGMENT = /^\d{6,}$/;

const UUID_VALUE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ERROR_CODE = /^[a-z][a-z0-9_]{0,63}$/;
const ERROR_KIND = /^[a-z_]{1,32}$/;
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
  'error.type': freeText(128),
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

/** Free text: the content rules first, then quoted names, then the length cap. */
function freeText(maxLength: number): AttributeRule {
  return (value) =>
    isText(value) ? scrubText(value, maxLength).replace(QUOTED_SUBSTRING, QUOTED) : undefined;
}

/**
 * A route template, or `undefined` when the value is not path-shaped at all. Segments that
 * are not a `:param`, `**`, or a static word become `:id` — so a route attribute that
 * somehow holds a concrete `/projects/p-1/runs` still leaves without the id.
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
    if (segment === '**' || PARAM_SEGMENT.test(segment)) {
      return segment;
    }
    // A static word stays only when it does not wear the shape of an identifier.
    const identifiersLike =
      UUID_SEGMENT.test(segment) || HEX_SEGMENT.test(segment) || DIGITS_SEGMENT.test(segment);
    return LITERAL_SEGMENT.test(segment) && identifiersLike === false ? segment : ID;
  });
  return `/${templated.join('/')}`;
}

/**
 * Removes credentials, identifiers and URL query strings from one line of free text and
 * truncates the result to `maxLength` (default 200) characters. Pure and non-throwing: any
 * input produces a string.
 */
export function scrubText(value: string, maxLength: number = DEFAULT_MAX_TEXT_LENGTH): string {
  let text = value;
  for (const rule of TEXT_RULES) {
    text = rule.apply(text);
  }
  const limit = Number.isFinite(maxLength)
    ? Math.max(0, Math.trunc(maxLength))
    : DEFAULT_MAX_TEXT_LENGTH;
  return text.length > limit ? text.slice(0, limit) : text;
}

/**
 * The scrubbed copy of a span's attributes: keys outside `ALLOWED_ATTRIBUTE_KEYS` are
 * dropped, values that are not a finite primitive are dropped, and every kept key is then
 * held to its own rule. The input is never mutated.
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
