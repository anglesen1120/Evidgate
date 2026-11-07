/**
 * The date-time shape `<time datetime>` and the `date` pipe render: an RFC 3339 date-time with a
 * numeric offset or `Z`. `Date.parse` alone is lenient enough to accept `'1'`, `'2026'` and
 * `'Nov 7 2026'`, which a surface must not show as a server instant.
 */
const INSTANT_SHAPE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;

/** {@link value} in milliseconds, or `null` when it is not an instant a surface can render. */
export function rfc3339InstantMs(value: string): number | null {
  if (!INSTANT_SHAPE.test(value)) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
