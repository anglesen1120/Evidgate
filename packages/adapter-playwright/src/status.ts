// Maps one Playwright attempt outcome onto the normalised status. The raw `TestResult.status` is
// never overwritten: it travels verbatim in the envelope `status` and the normalised value goes
// to `ext.normalized`. The mapping is pure and table-driven; anything the table does not know —
// a future Playwright status, an empty string — maps to `error` and is flagged `unknown`, so the
// reporter can emit an `unknown_status` warning instead of guessing.
//
// `ext.attempt` is deliberately not emitted: the envelope's own `attempt_no` carries the attempt,
// and a second copy could disagree.

/** The statuses this adapter produces (`blocked` is server-side only). */
export type NormalizedStatus = 'passed' | 'failed' | 'skipped' | 'timed_out' | 'error';

export interface StatusInput {
  /** `TestResult.status`, raw; may be a value a future Playwright adds. */
  status: string;
  /** `TestCase.expectedStatus` (`failed` for `test.fail()`). */
  expectedStatus?: string;
  /** `test.annotations` followed by `result.annotations`. */
  annotations: readonly { type: string; description?: string }[];
  /** `TestResult.errors`. */
  errors?: readonly { message?: string; value?: string }[];
}

export interface StatusMapping {
  /** The envelope `status`: the raw value verbatim, cut to the schema's 64 chars. */
  raw: string;
  normalized: NormalizedStatus;
  /** Set only for a raw `skipped` whose annotations include `fixme`. */
  skip_reason?: 'fixme';
  /** Set when `expectedStatus` is `failed` and the attempt passed or failed. */
  xfail?: true;
  /** The raw status is not a key of {@link STATUS_TABLE}; `normalized` is `error`. */
  unknown?: true;
  /** First line of the first error message, ANSI-stripped, at most 256 chars. */
  error_summary?: string;
}

/** Raw Playwright status → normalised status. Looked up by own property only. */
export const STATUS_TABLE: Readonly<Record<string, NormalizedStatus>> = Object.freeze({
  passed: 'passed',
  failed: 'failed',
  timedOut: 'timed_out',
  interrupted: 'error',
  skipped: 'skipped',
});

/** The most UTF-16 units `ext.error_summary` carries. */
export const ERROR_SUMMARY_MAX = 256;

/** `contracts/schemas/event-envelope.v1.json` `status.maxLength`. */
const RAW_STATUS_MAX = 64;

/** CSI sequences (colours, cursor moves) and OSC sequences (hyperlinks, titles). */
const ANSI_PATTERN = /\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)?/g;

/** CR/LF pairs and every single-character line terminator, incl. NEL and U+2028/U+2029. */
const LINE_BREAK = /\r\n|[\n\r\u0085\u2028\u2029]/;
const LINE_BREAKS = /\r\n|[\n\r\u0085\u2028\u2029]/g;

/**
 * SEC-1: control characters (lone ESC, C1 such as U+009B, NUL, BEL, VT, FF) and format
 * characters (BIDI overrides, zero-width marks) left after the ANSI strip — the
 * `sidecarRules.ts` UNCLEAN precedent. Removed, so a log viewer never sees a live escape.
 */
const UNSAFE_CHARS = /[\p{Cc}\p{Cf}]/gu;

/**
 * PERF-1: the most UTF-16 units of one error read before stripping. A first line longer
 * than this is cut at {@link ERROR_SUMMARY_MAX} anyway, so a multi-megabyte message costs
 * a bounded scan instead of a split over every line.
 */
const ERROR_SCAN_MAX = 16 * ERROR_SUMMARY_MAX;

/**
 * A URL's userinfo and its query/fragment — Playwright's own first lines embed the call URL
 * (`page.goto: net::ERR_… at https://host/path?token=…`), which can carry a credential.
 * Scheme, host and path stay.
 */
const URL_PATTERN = /\b([a-z][a-z0-9+.-]*:\/\/)([^\s/?#@]*@)?([^\s?#]*)([?#]\S*)?/gi;

/**
 * Maps one attempt. Rules, in order:
 * - a raw status that is not an own key of {@link STATUS_TABLE} → `error`, `unknown`;
 * - `expectedStatus: 'failed'` × raw `failed` → `passed`, `xfail` (an expected failure);
 * - `expectedStatus: 'failed'` × raw `passed` → `failed`, `xfail` (an unexpected pass
 *   fails closed: it is never reported as PASS);
 * - raw `skipped` with a `fixme` annotation → `skipped`, `skip_reason: 'fixme'`
 *   (`fixme` wins over `skip`);
 * - otherwise the table row.
 * `error_summary` is set for any outcome whose errors carry text.
 */
export function mapStatus(input: StatusInput): StatusMapping {
  // A non-string at runtime (a malformed fake or a future shape) is treated as unknown.
  const status = typeof input.status === 'string' ? input.status : '';
  const mapping: StatusMapping = { raw: cutUtf16(status, RAW_STATUS_MAX), normalized: 'error' };
  const row = Object.hasOwn(STATUS_TABLE, status) ? STATUS_TABLE[status] : undefined;
  if (row === undefined) {
    mapping.unknown = true;
  } else if (input.expectedStatus === 'failed' && status === 'failed') {
    mapping.normalized = 'passed';
    mapping.xfail = true;
  } else if (input.expectedStatus === 'failed' && status === 'passed') {
    mapping.normalized = 'failed';
    mapping.xfail = true;
  } else {
    mapping.normalized = row;
    if (status === 'skipped' && input.annotations.some((item) => item.type === 'fixme')) {
      mapping.skip_reason = 'fixme';
    }
  }
  const summary = errorSummary(input.errors);
  if (summary !== undefined) mapping.error_summary = summary;
  return mapping;
}

/** Playwright's zero-based `retry` → the envelope's one-based `attempt_no`. */
export function attemptOf(result: { retry: number }): number {
  return result.retry + 1;
}

/**
 * The first line of the first error whose message (else value) has text: read from at
 * most {@link ERROR_SCAN_MAX} units, ANSI escape sequences removed, cut at the first line
 * terminator, control and format characters removed, trimmed, URL userinfo and
 * query/fragment redacted, and capped at {@link ERROR_SUMMARY_MAX}. Never a stack line:
 * only the first line of the message is read. `undefined` when no error carries text.
 */
export function errorSummary(errors: StatusInput['errors']): string | undefined {
  for (const error of errors ?? []) {
    const text: unknown = error?.message ?? error?.value;
    if (typeof text !== 'string') continue;
    const stripped = cutUtf16(text, ERROR_SCAN_MAX).replace(ANSI_PATTERN, '');
    const firstLine = printable(stripped.split(LINE_BREAK, 1)[0] ?? '').trim();
    if (firstLine.length === 0) continue;
    // Redact the whole (≤ ERROR_SCAN_MAX) line before the cap, so a cut can never fall
    // inside userinfo and leave a credential prefix unredacted.
    const redacted = firstLine.replace(
      URL_PATTERN,
      (_match, scheme: string, userinfo: string | undefined, rest: string, tail?: string) =>
        `${scheme}${userinfo === undefined ? '' : '[redacted]@'}${rest}` +
        (tail === undefined ? '' : `${tail[0]}[redacted]`),
    );
    return cutUtf16(redacted, ERROR_SUMMARY_MAX);
  }
  return undefined;
}

/**
 * SEC-1/SEC-3: `value` with line terminators and tabs turned into spaces and every other
 * control or format character removed — safe to embed in one log line.
 */
export function printable(value: string): string {
  return value.replace(LINE_BREAKS, ' ').replace(/\t/g, ' ').replace(UNSAFE_CHARS, '');
}

/** At most `max` UTF-16 units, never ending on a lone high surrogate. */
function cutUtf16(value: string, max: number): string {
  if (value.length <= max) return value;
  const last = value.charCodeAt(max - 1);
  return value.slice(0, last >= 0xd800 && last <= 0xdbff ? max - 1 : max);
}
