// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, ids
// only): the schema filter every span attribute passes through before it leaves the app.
//
// One layer, not two, and no regular-expression text scrubber anywhere in the telemetry path.
// That is a property of the design, not a gap:
// - a span *name* is admitted only when it is one of the fixed constants of `telemetry-names.ts`
//   (`SPAN_NAMES`), and an event only when its name is in `EVENT_NAMES` (one name:
//   `audit.export_capped`), so a name that carries content — a URL, a message, an e-mail address —
//   is dropped rather than rewritten (`scrubbing-exporter.ts`);
// - D-S69-Q2 removed `error.message`, so no attribute carries free text at all: a client error
//   reports its kind, code, status and retryable flag and nothing else;
// - what remains is a closed set of structured values, which is what this file filters: an
//   allowlist of keys, then one rule per key — a method token, a status range, a route *template*,
//   an enum, a boolean, a finite number. A route keeps its static words and loses its ids, and a
//   URL never enters an attribute in the first place (the API path is templated by
//   `api-path-template.ts` before it is recorded).
//
// Pure, total and non-throwing: telemetry loss must never break the app, so a value the rules
// cannot understand is dropped rather than reported.
//
// Known limit, by construction: the ids in a route path are recognised by shape, so a slug made of
// plain words keeps those words (`/projects/acme-corp/runs`), because a static route word and a slug
// look alike. Concrete literals are the router's business: `route-template.ts` builds the template
// from `routeConfig.path` and never reads a parameter value.
import type { Attributes } from '@opentelemetry/api';

import { ALLOWED_ATTRIBUTE_KEYS } from './attribute-allowlist';

/** A route template longer than this is not a route template. */
const MAX_ROUTE_LENGTH = 300;

/** The segment a route id becomes. */
const ID = ':id';

/** A value the SDK can serialize; anything else (object, array, `undefined`) is dropped. */
type SimpleValue = string | number | boolean;

/** One attribute key's rule: the cleaned value, or `undefined` to drop the key. */
type AttributeRule = (value: SimpleValue) => SimpleValue | undefined;

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
  // No `error.message`: D-S69-Q2 says a client error carries no message text at all, so the key is
  // not allowlisted either and `scrubAttributes` drops it on the key before any rule could run.
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

/**
 * A route template, or `undefined` when the value is not path-shaped at all. A segment stays
 * only when it *is* template syntax — a `:param`, `**`, a version prefix — or a plain lowercase
 * word (`projects`, `runs`). Any digit makes a segment an id, so a concrete
 * `/projects/acme-corp/runs/p-1` leaves as `/projects/acme-corp/runs/:id`: the id goes, while a
 * slug made of plain words keeps its words, because nothing tells it apart from a static route
 * word. A dotted or underscored segment (`julia.moreau`) is an identifier, never a word.
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
