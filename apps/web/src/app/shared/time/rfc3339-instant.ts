/**
 * The date-time shape `<time datetime>` and the `date` pipe render: an RFC 3339 date-time with a
 * numeric offset or `Z`. `Date.parse` alone is lenient enough to accept `'1'`, `'2026'` and
 * `'Nov 7 2026'`, which a surface must not show as a server instant.
 *
 * The date and time components are captured because `Date.parse` does not refuse a calendar
 * impossibility: it rolls `2026-02-30` and `24:00:00` into the next day, so the shape match alone
 * would publish a surface date the server never sent.
 */
const INSTANT_SHAPE =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/;

/** The length of each Gregorian month in a common year; February is lengthened by the leap rule. */
const MONTH_LENGTHS: readonly number[] = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

/** The proleptic Gregorian leap rule, so `2028-02-29` is read and `2026-02-29` is not. */
function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

/** {@link value} in milliseconds, or `null` when it is not an instant a surface can render. */
export function rfc3339InstantMs(value: string): number | null {
  const match = INSTANT_SHAPE.exec(value);
  if (match === null) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  // A month outside the table (00, 13) has no length, so the range check refuses it here.
  const monthLength = MONTH_LENGTHS[month - 1];
  if (monthLength === undefined) return null;
  const lastDay = month === 2 && isLeapYear(year) ? 29 : monthLength;
  if (day < 1 || day > lastDay) return null;
  // A second is 00-59 in RFC 3339: a leap second and `24:00:00` are both refused, as the parsing
  // fallback would roll them into the following second or day.
  if (Number(match[4]) > 23 || Number(match[5]) > 59 || Number(match[6]) > 59) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
